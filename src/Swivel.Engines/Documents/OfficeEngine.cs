using System.Runtime.InteropServices;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Infrastructure;

namespace Swivel.Engines.Documents;

/// <summary>
/// Anything printable to PDF. Office files go through Microsoft Office over COM when it's installed,
/// otherwise LibreOffice headless (installed, or the optional pack). Markdown, HTML, text and EPUB
/// are first turned into DOCX by Pandoc.
/// </summary>
public sealed class OfficeEngine : IEngine
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public string Name => Operation.Engines.Office;

    public async Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct)
    {
        string input = job.Inputs[0];
        var kind = FileSniffer.Detect(input);
        using var scratch = new Scratch();

        string source = input;
        if (kind is FileKind.Markdown or FileKind.Html or FileKind.Txt or FileKind.Epub)
        {
            source = scratch.File(Path.GetFileNameWithoutExtension(input) + ".docx");
            await DocumentEngine.ConvertAsync(input, FileKind.Docx, source, ct);
            kind = FileKind.Docx;
            progress.Report(0.3);
        }

        string pdf = scratch.File("out.pdf");
        await OneAtATime.WaitAsync(ct);
        try
        {
            if (OfficeApp(kind) is { } progId && Type.GetTypeFromProgID(progId) is { } type)
            {
                try
                {
                    await RunOnStaAsync(() => ExportWithOffice(type, kind, source, pdf), ct);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or TimeoutException && LibreOffice() != null)
                {
                    // Office is present but couldn't do it (unlicensed, blocked dialog): try LibreOffice.
                    await ExportWithLibreOffice(source, scratch, pdf, ct);
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                    throw new JobException("Microsoft Office couldn't open it. Is Office activated?", ex);
                }
                catch (TimeoutException ex)
                {
                    throw new JobException("Office took over three minutes, perhaps waiting on a dialog, so Swivel gave up.", ex);
                }
            }
            else
            {
                await ExportWithLibreOffice(source, scratch, pdf, ct);
            }
        }
        finally
        {
            OneAtATime.Release();
        }

        if (!File.Exists(pdf)) throw new JobException($"Couldn't print {Path.GetFileName(input)} to PDF.");
        File.Move(pdf, job.TempPath!);
        progress.Report(1);
    }

    private static string? OfficeApp(FileKind kind) => kind switch
    {
        FileKind.Docx => "Word.Application",
        FileKind.Xlsx => "Excel.Application",
        FileKind.Pptx => "PowerPoint.Application",
        _ => null,
    };

    /// <summary>LibreOffice's soffice.exe: Swivel's pack first, then a normal install.</summary>
    public static string? LibreOffice()
    {
        if (EnginePacks.Locate(EnginePacks.LibreOffice) is { } pack) return pack;
        foreach (var root in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string candidate = Path.Combine(Environment.GetFolderPath(root), "LibreOffice", "program", "soffice.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task ExportWithLibreOffice(string source, Scratch scratch, string pdf, CancellationToken ct)
    {
        string soffice = LibreOffice() ?? throw new PackMissingException(EnginePacks.LibreOffice,
            "Office to PDF needs Microsoft Office or the free LibreOffice pack (350 MB).");
        string profile = new Uri(Path.Combine(scratch.Path, "profile")).AbsoluteUri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var run = await ProcessRunner.RunAsync(soffice,
            [$"-env:UserInstallation={profile}", "--headless", "--norestore", "--nologo", "--convert-to", "pdf", "--outdir", scratch.Path, source],
            timeout.Token);
        string produced = Path.Combine(scratch.Path, Path.GetFileNameWithoutExtension(source) + ".pdf");
        if (run.ExitCode != 0 || !File.Exists(produced))
            throw new JobException($"Couldn't print {Path.GetFileName(source)} to PDF.", new InvalidOperationException(run.StandardError));
        File.Move(produced, pdf, overwrite: true);
    }

    /// <summary>Opens read-only, exports, closes without saving, and only quits an app with nothing else open.</summary>
    private static void ExportWithOffice(Type type, FileKind kind, string source, string pdf)
    {
        dynamic app = Activator.CreateInstance(type)!;
        dynamic? doc = null;
        try
        {
            switch (kind)
            {
                case FileKind.Docx:
                    app.Visible = false;
                    app.DisplayAlerts = 0; // wdAlertsNone
                    doc = app.Documents.Open(source, false, true, false); // ConfirmConversions, ReadOnly, AddToRecentFiles
                    doc.ExportAsFixedFormat(pdf, 17); // wdExportFormatPDF
                    doc.Close(0); // wdDoNotSaveChanges
                    doc = null;
                    if (app.Documents.Count == 0) app.Quit(0);
                    break;
                case FileKind.Xlsx:
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    doc = app.Workbooks.Open(source, 0, true); // UpdateLinks, ReadOnly
                    doc.ExportAsFixedFormat(0, pdf); // xlTypePDF
                    doc.Close(false);
                    doc = null;
                    if (app.Workbooks.Count == 0) app.Quit();
                    break;
                case FileKind.Pptx:
                    doc = app.Presentations.Open(source, -1, 0, 0); // ReadOnly, Untitled, WithWindow
                    doc.SaveAs(pdf, 32); // ppSaveAsPDF
                    doc.Close();
                    doc = null;
                    if (app.Presentations.Count == 0) app.Quit();
                    break;
            }
        }
        finally
        {
            if (doc != null)
            {
                try { doc.Close(0); } catch (COMException) { }
                Marshal.FinalReleaseComObject(doc);
            }
            Marshal.FinalReleaseComObject(app);
            doc = null;
            app = null!;
            // Late binding leaves wrappers for collections (Documents, Workbooks…) that keep a hidden
            // Office process alive until they're collected, so collect them now.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static Task RunOnStaAsync(Action work, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { work(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        })
        { IsBackground = true, Name = "Swivel.Office" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(Timeout, ct);
    }
}
