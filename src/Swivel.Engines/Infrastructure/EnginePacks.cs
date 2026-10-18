using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using Swivel.Core.Jobs;

namespace Swivel.Engines.Infrastructure;

/// <summary>An engine that isn't in the core install and downloads on first need.</summary>
public sealed record EnginePack(string Id, string Name, string Url, string Sha256, long DownloadBytes, string ProbeFile, PackLayout Layout);

public enum PackLayout
{
    /// <summary>A zip whose single top folder's files go into the pack folder.</summary>
    ZipTopFolder,

    /// <summary>An MSI unpacked per user with an administrative install (no elevation, no system changes).</summary>
    MsiAdminExtract,
}

/// <summary>Thrown when a job needs a pack that isn't installed yet. The toast offers to fetch it.</summary>
public sealed class PackMissingException(EnginePack pack, string message) : JobException(message)
{
    public const string Scheme = "swivel-pack:";

    public EnginePack Pack { get; } = pack;

    public override string? HelpUrl => Scheme + Pack.Id;
}

public static class EnginePacks
{
    // Hashes match build/engines.lock.json; EnginePackTests checks they agree.
    public static EnginePack Pandoc { get; } = new(
        "pandoc", "Pandoc",
        "https://github.com/jgm/pandoc/releases/download/3.12.1/pandoc-3.12.1-windows-x86_64.zip",
        "6ef431cb20b2c24a0371fc19200dc49d452be54b1b5c9239f3462841ef53e587",
        42_200_000, @"pandoc\pandoc.exe", PackLayout.ZipTopFolder);

    public static EnginePack LibreOffice { get; } = new(
        "libreoffice", "LibreOffice",
        "https://download.documentfoundation.org/libreoffice/stable/26.2.6/win/x86_64/LibreOffice_26.2.6_Win_x86-64.msi",
        "f9877032fd908beb9c0ddf06df4af5c2e85f419c42e14876c4cce5aae5fb2660", 373_252_096,
        @"libreoffice\program\soffice.exe", PackLayout.MsiAdminExtract);

    public static IReadOnlyList<EnginePack> All { get; } = [Pandoc, LibreOffice];

    public static EnginePack? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static string? Locate(EnginePack pack) => EngineLocator.Find(pack.ProbeFile);

    public static string Require(EnginePack pack, string why) =>
        Locate(pack) ?? throw new PackMissingException(pack, why);

    /// <summary>Downloads, verifies and unpacks a pack into the packs folder.</summary>
    public static async Task InstallAsync(EnginePack pack, IProgress<double> progress, CancellationToken ct)
    {
        string root = Path.Combine(EngineLocator.PacksRoot, pack.Id);
        string staging = root + ".download";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        string file = Path.Combine(staging, Path.GetFileName(new Uri(pack.Url).AbsolutePath));

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            using (var response = await http.GetAsync(pack.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? pack.DownloadBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(file);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress.Report(0.9 * done / Math.Max(total, 1));
                }
            }

            if (pack.Sha256.Length > 0)
            {
                await using var stream = File.OpenRead(file);
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (hash != pack.Sha256) throw new JobException($"The {pack.Name} download didn't match its fingerprint, so Swivel threw it away.");
            }

            string unpacked = Path.Combine(staging, "unpacked");
            switch (pack.Layout)
            {
                case PackLayout.ZipTopFolder:
                    ZipFile.ExtractToDirectory(file, unpacked);
                    var top = Directory.GetDirectories(unpacked).Single();
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    Directory.Move(top, root);
                    break;
                case PackLayout.MsiAdminExtract:
                    var result = await ProcessRunner.RunAsync("msiexec.exe", ["/a", file, "/qn", $"TARGETDIR={unpacked}"], ct);
                    if (result.ExitCode != 0) throw new JobException($"Unpacking {pack.Name} failed (msiexec {result.ExitCode}).");
                    string soffice = Directory.GetFiles(unpacked, "soffice.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "program")
                        ?? throw new JobException($"{pack.Name} unpacked without soffice.exe.");
                    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
                    Directory.Move(Path.GetDirectoryName(Path.GetDirectoryName(soffice))!, root);
                    break;
            }
            progress.Report(1);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        if (Locate(pack) is null) throw new JobException($"{pack.Name} downloaded but its files aren't where Swivel expects.");
        Trace.WriteLine($"Installed pack {pack.Id}");
    }
}
