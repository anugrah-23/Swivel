using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using Swivel.Core.Catalog;
using Swivel.Core.Files;
using Swivel.Core.Jobs;
using Swivel.Engines.Infrastructure;

namespace Swivel.Engines.Archives;

/// <summary>
/// ZIP, TAR and GZIP through SharpCompress; 7z creation through 7-Zip's standalone 7zr.exe.
/// Extraction refuses any entry that would land outside the output folder.
/// </summary>
public sealed partial class ArchiveEngine : IEngine
{
    public string Name => Operation.Engines.Archive;

    public Task RunAsync(Job job, IProgress<double> progress, CancellationToken ct) => job.Operation.Id switch
    {
        "archive.extract" => Task.Run(() => Extract(job.Inputs[0], job.TempPath!, progress, ct), ct),
        "archive.7z" => SevenZipAsync(job.Inputs, job.TempPath!, progress, ct),
        "archive.zip" => Task.Run(() => Create(job.Inputs, job.TempPath!, ArchiveType.Zip, progress, ct), ct),
        "archive.tar" => Task.Run(() => Create(job.Inputs, job.TempPath!, ArchiveType.Tar, progress, ct), ct),
        "archive.gz" => Task.Run(() => Create(job.Inputs, job.TempPath!, ArchiveType.GZip, progress, ct), ct),
        _ => throw new JobException($"{job.Operation.Label} isn't built yet."),
    };

    // ---- Create ---------------------------------------------------------------------------------

    private sealed record Entry(string Source, string Name, bool IsDirectory);

    /// <summary>Files keep their name; folders keep their name as the top level of their contents.</summary>
    private static List<Entry> Collect(IReadOnlyList<string> inputs)
    {
        var entries = new List<Entry>();
        foreach (string raw in inputs)
        {
            string input = Path.GetFullPath(raw).TrimEnd(Path.DirectorySeparatorChar);
            if (Directory.Exists(input))
            {
                string root = Path.GetDirectoryName(input)!;
                var dir = new DirectoryInfo(input);
                entries.Add(new Entry(input, Relative(root, input) + "/", true));
                foreach (var item in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    entries.Add(item is DirectoryInfo
                        ? new Entry(item.FullName, Relative(root, item.FullName) + "/", true)
                        : new Entry(item.FullName, Relative(root, item.FullName), false));
            }
            else if (File.Exists(input))
            {
                entries.Add(new Entry(input, Path.GetFileName(input), false));
            }
        }
        return entries;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void Create(IReadOnlyList<string> inputs, string output, ArchiveType type, IProgress<double> progress, CancellationToken ct)
    {
        var entries = Collect(inputs);
        if (type == ArchiveType.GZip && (entries.Count != 1 || entries[0].IsDirectory))
            throw new JobException("GZIP holds a single file. Try ZIP or 7Z for more.");
        long total = Math.Max(1, entries.Where(e => !e.IsDirectory).Sum(e => new FileInfo(e.Source).Length));
        long done = 0;

        WriterOptions options = type switch
        {
            ArchiveType.Zip => WriterOptions.ForZip(CompressionType.Deflate),
            ArchiveType.Tar => WriterOptions.ForTar(CompressionType.None),
            _ => WriterOptions.ForGZip(),
        };
        using var stream = File.Create(output);
        using var writer = WriterFactory.OpenWriter(stream, type, options);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
            {
                if (type != ArchiveType.GZip) writer.WriteDirectory(entry.Name.TrimEnd('/'), Directory.GetLastWriteTime(entry.Source));
                continue;
            }
            using var source = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            writer.Write(entry.Name, source, File.GetLastWriteTime(entry.Source));
            done += source.Length;
            progress.Report((double)done / total);
        }
    }

    private static async Task SevenZipAsync(IReadOnlyList<string> inputs, string output, IProgress<double> progress, CancellationToken ct)
    {
        string sevenZip = EngineLocator.Find(Path.Combine("7zip", "7zr.exe"))
            ?? throw new JobException("7-Zip is missing from this install. Reinstall Swivel to get it back.");
        using var scratch = new Scratch();
        string archive = scratch.File("out.7z");
        var args = new List<string> { "a", "-t7z", "-mx=7", "-bsp1", "-bso0", "-y", "-snl-", archive };
        args.AddRange(inputs.Select(i => Path.GetFullPath(i).TrimEnd(Path.DirectorySeparatorChar)));

        var run = await ProcessRunner.RunAsync(sevenZip, args, ct, line =>
        {
            var match = Percent().Match(line);
            if (match.Success) progress.Report(int.Parse(match.Groups[1].Value) / 100.0);
        });
        if (run.ExitCode != 0 || !File.Exists(archive))
            throw new JobException("7-Zip couldn't pack those files.", new InvalidOperationException(run.StandardError));
        File.Move(archive, output);
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();

    // ---- Extract --------------------------------------------------------------------------------

    private static void Extract(string input, string folder, IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fallbackName = Path.GetFileNameWithoutExtension(input);

        using var archive = ArchiveFactory.OpenArchive(input, new ReaderOptions());
        long total = Math.Max(1, archive.TotalUncompressedSize);
        long done = 0;

        // Solid archives (most 7z) must be read front to back; the rest can be read entry by entry.
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
                done += Write(reader.Entry, reader.WriteEntryTo);
        }
        else
        {
            foreach (var entry in archive.Entries)
                done += Write(entry, file => { using var stream = entry.OpenEntryStream(); stream.CopyTo(file); });
        }

        FlattenSingleFolder(folder);

        long Write(IEntry entry, Action<Stream> copy)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsEncrypted) throw new JobException($"{Path.GetFileName(input)} is password-protected.");
            if (!string.IsNullOrEmpty(entry.LinkTarget)) return 0; // never follow links out of the folder

            string name = string.IsNullOrEmpty(entry.Key) ? fallbackName : entry.Key;
            string target = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new JobException($"{Path.GetFileName(input)} tries to write outside its folder, so Swivel stopped.");

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                return 0;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var file = File.Create(target)) copy(file);
            if (entry.LastModifiedTime is DateTime modified) File.SetLastWriteTime(target, modified);
            progress.Report(Math.Min(0.99, (double)(done + entry.Size) / total));
            return entry.Size;
        }
    }

    /// <summary>"photos.zip" holding just "photos/…" unpacks to "photos/…", not "photos/photos/…".</summary>
    private static void FlattenSingleFolder(string folder)
    {
        var dirs = Directory.GetDirectories(folder);
        if (dirs.Length != 1 || Directory.GetFiles(folder).Length != 0) return;
        string inner = dirs[0];
        foreach (var item in new DirectoryInfo(inner).EnumerateFileSystemInfos())
        {
            string dest = Path.Combine(folder, item.Name);
            if (string.Equals(dest, inner, StringComparison.OrdinalIgnoreCase)) return; // name clash; leave nested
        }
        foreach (var item in new DirectoryInfo(inner).EnumerateFileSystemInfos())
        {
            string dest = Path.Combine(folder, item.Name);
            if (item is DirectoryInfo d) d.MoveTo(dest); else ((FileInfo)item).MoveTo(dest);
        }
        Directory.Delete(inner);
    }
}
