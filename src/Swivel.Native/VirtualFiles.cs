using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Swivel.Native;

/// <summary>
/// Reads "virtual" files (FileGroupDescriptorW + FileContents) dragged from browsers, Outlook and
/// Teams, which carry content rather than paths. Contents are written to a temp folder first.
/// </summary>
public static partial class VirtualFiles
{
    public const string DescriptorFormat = "FileGroupDescriptorW";
    public const string ContentsFormat = "FileContents";

    private const int DescriptorSize = 592, NameOffset = 72, AttributesOffset = 36, SizeHighOffset = 64;
    private const uint FD_ATTRIBUTES = 0x4, FD_FILESIZE = 0x40, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    public sealed record Entry(string Name, bool IsDirectory, long? Size);

    public static IReadOnlyList<Entry> ReadDescriptor(Stream descriptor)
    {
        using var reader = new BinaryReader(descriptor, Encoding.Unicode, leaveOpen: true);
        uint count = reader.ReadUInt32();
        var entries = new List<Entry>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
        {
            byte[] raw = reader.ReadBytes(DescriptorSize);
            if (raw.Length < DescriptorSize) break;
            uint flags = BitConverter.ToUInt32(raw, 0);
            uint attributes = BitConverter.ToUInt32(raw, AttributesOffset);
            string name = Encoding.Unicode.GetString(raw, NameOffset, DescriptorSize - NameOffset);
            int nul = name.IndexOf('\0');
            if (nul >= 0) name = name[..nul];
            long? size = (flags & FD_FILESIZE) != 0
                ? ((long)BitConverter.ToUInt32(raw, SizeHighOffset) << 32) | BitConverter.ToUInt32(raw, SizeHighOffset + 4)
                : null;
            bool isDir = (flags & FD_ATTRIBUTES) != 0 && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            entries.Add(new Entry(name, isDir, size));
        }
        return entries;
    }

    /// <summary>Writes every file entry into <paramref name="targetDir"/> and returns the top-level paths.</summary>
    public static IReadOnlyList<string> Extract(ComDataObject data, IReadOnlyList<Entry> entries, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        string root = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        short cf = unchecked((short)RegisterClipboardFormatW(ContentsFormat));
        var topLevel = new List<string>();

        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            string path = Path.GetFullPath(Path.Combine(root, Sanitize(entry.Name)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                path = Unique(path);
                using var output = File.Create(path);
                CopyContents(data, cf, i, entry.Size, output);
            }

            if (!entry.Name.Contains('\\') && !entry.Name.Contains('/')) topLevel.Add(path);
        }
        return topLevel;
    }

    private static void CopyContents(ComDataObject data, short cf, int index, long? size, Stream output)
    {
        var format = new FORMATETC
        {
            cfFormat = cf,
            dwAspect = DVASPECT.DVASPECT_CONTENT,
            lindex = index,
            tymed = TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
        };
        data.GetData(ref format, out STGMEDIUM medium);
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                var stream = (IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                try { CopyStream(stream, output); }
                finally { Marshal.ReleaseComObject(stream); }
            }
            else if (medium.tymed == TYMED.TYMED_HGLOBAL)
            {
                nint ptr = GlobalLock(medium.unionmember);
                try
                {
                    long length = (long)GlobalSize(medium.unionmember);
                    if (size is long known && known < length) length = known;
                    unsafe
                    {
                        using var source = new UnmanagedMemoryStream((byte*)ptr, length);
                        source.CopyTo(output);
                    }
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    private static unsafe void CopyStream(IStream stream, Stream output)
    {
        byte[] buffer = new byte[81920];
        int read;
        do
        {
            read = 0;
            stream.Read(buffer, buffer.Length, (nint)(&read));
            output.Write(buffer, 0, read);
        } while (read > 0);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var parts = name.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(p => p is not "." and not "..")
            .Select(p => string.Concat(p.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c)));
        string joined = string.Join(Path.DirectorySeparatorChar, parts);
        return joined.Length == 0 ? "file" : joined;
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int n = 1; ; n++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string format);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint hMem);

#pragma warning disable SYSLIB1054 // STGMEDIUM holds an object reference, so it needs runtime marshalling.
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
#pragma warning restore SYSLIB1054
}
