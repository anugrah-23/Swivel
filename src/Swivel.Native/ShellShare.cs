using System.Runtime.InteropServices;

namespace Swivel.Native;

/// <summary>
/// Opens the Windows share sheet (Nearby Share, Mail, apps) for files by invoking Explorer's own
/// "Windows.ModernShare" verb, the same one behind its Share button.
/// </summary>
public static partial class ShellShare
{
    private const uint CMF_NORMAL = 0, CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_ASYNCOK = 0x100000;
    private static readonly Guid BHID_SFUIObject = new("3981E225-F559-11D3-8E3A-00C04F6837D5");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    [ComImport, Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig]
        int BindToHandler(nint bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(nint menu, uint index, uint firstId, uint lastId, uint flags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public nint hIcon;
        public nint lpTitle;
        public nint lpVerbW;
        public nint lpParametersW;
        public nint lpDirectoryW;
        public nint lpTitleW;
        public Win32.POINT ptInvoke;
    }

    /// <summary>Shows the share sheet for files in one folder. Returns false if Windows has no share verb.</summary>
    public static bool Share(IReadOnlyList<string> paths, nint ownerWindow)
    {
        var pidls = new List<nint>();
        nint ansiVerb = Marshal.StringToHGlobalAnsi("Windows.ModernShare");
        nint wideVerb = Marshal.StringToHGlobalUni("Windows.ModernShare");
        nint menu = CreatePopupMenu();
        object? arrayObject = null, menuObject = null;
        try
        {
            foreach (string path in paths)
                if (SHParseDisplayName(Path.GetFullPath(path), 0, out nint pidl, 0, out _) == 0) pidls.Add(pidl);
            if (pidls.Count == 0) return false;
            if (SHCreateShellItemArrayFromIDLists((uint)pidls.Count, [.. pidls], out arrayObject) != 0) return false;

            Guid handler = BHID_SFUIObject, iid = IID_IContextMenu;
            if (((IShellItemArray)arrayObject).BindToHandler(0, ref handler, ref iid, out menuObject) != 0) return false;
            var contextMenu = (IContextMenu)menuObject;
            contextMenu.QueryContextMenu(menu, 0, 1, 0x7FFF, CMF_NORMAL);

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_ASYNCOK,
                hwnd = ownerWindow,
                lpVerb = ansiVerb,
                lpVerbW = wideVerb,
                nShow = 1,
            };
            return contextMenu.InvokeCommand(ref info) == 0;
        }
        finally
        {
            if (menuObject != null) Marshal.ReleaseComObject(menuObject);
            if (arrayObject != null) Marshal.ReleaseComObject(arrayObject);
            DestroyMenu(menu);
            foreach (nint pidl in pidls) Marshal.FreeCoTaskMem(pidl);
            Marshal.FreeHGlobal(ansiVerb);
            Marshal.FreeHGlobal(wideVerb);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, nint bindingContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

#pragma warning disable SYSLIB1054 // returns a COM object, which needs runtime marshalling
    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromIDLists(uint count, nint[] pidls, [MarshalAs(UnmanagedType.IUnknown)] out object array);
#pragma warning restore SYSLIB1054

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);
}
