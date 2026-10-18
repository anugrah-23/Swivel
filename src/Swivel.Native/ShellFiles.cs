using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Swivel.Native;

/// <summary>Recycle Bin and "show in folder" through the Windows shell.</summary>
public static partial class ShellFiles
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    /// <summary>Sends files to the Recycle Bin silently, so the user can still restore them.</summary>
    public static void Recycle(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var op = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int result = SHFileOperationW(ref op);
        if (result != 0) throw new Win32Exception(result, "Couldn't move the files to the Recycle Bin.");
        if (op.fAnyOperationsAborted) throw new OperationCanceledException("Recycling was aborted.");
    }

    /// <summary>Opens Explorer on the files' folder with the files selected. Call from an STA thread.</summary>
    public static void Reveal(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        string folder = Path.GetDirectoryName(Path.GetFullPath(paths[0]))!;
        var inFolder = paths.Select(Path.GetFullPath)
            .Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (SHParseDisplayName(folder, 0, out nint folderPidl, 0, out _) != 0) return;
        var items = new List<nint>();
        try
        {
            foreach (string path in inFolder)
                if (SHParseDisplayName(path, 0, out nint pidl, 0, out _) == 0) items.Add(pidl);
            SHOpenFolderAndSelectItems(folderPidl, (uint)items.Count, [.. items], 0);
        }
        finally
        {
            foreach (nint pidl in items) Marshal.FreeCoTaskMem(pidl);
            Marshal.FreeCoTaskMem(folderPidl);
        }
    }

#pragma warning disable SYSLIB1054 // The struct carries strings, so runtime marshalling is simplest.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW op);
#pragma warning restore SYSLIB1054

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindingContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(nint folderPidl, uint count, nint[] items, uint flags);
}
