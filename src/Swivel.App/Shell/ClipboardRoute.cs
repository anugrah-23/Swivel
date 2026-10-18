using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Serilog;
using Swivel.App.Services;

namespace Swivel.App.Shell;

/// <summary>
/// Ctrl+Alt+V: copied files go straight to the wheel; a copied picture or text is first saved as a
/// file in Swivel's scratch folder (its outputs land in Downloads).
/// </summary>
public static class ClipboardRoute
{
    public static IReadOnlyList<string> Materialize()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return Read();
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(40); // another app is holding the clipboard open
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                Log.Warning(ex, "Clipboard was busy");
                return [];
            }
        }
    }

    private static IReadOnlyList<string> Read()
    {
        if (Clipboard.ContainsFileDropList())
            return Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();

        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
        string folder = Path.Combine(AppPaths.Drops, Guid.NewGuid().ToString("N"));

        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"Clipboard image {stamp}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
            return [path];
        }

        if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
        {
            Directory.CreateDirectory(folder);
            bool markdown = text.Contains("\n# ") || text.StartsWith("# ") || text.Contains("\n- ") || text.Contains("**");
            string path = Path.Combine(folder, $"Clipboard text {stamp}{(markdown ? ".md" : ".txt")}");
            File.WriteAllText(path, text);
            return [path];
        }
        return [];
    }
}
