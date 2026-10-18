using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

[assembly: DisableRuntimeMarshalling]

namespace Swivel.ShellExt;

/// <summary>Explorer's context-menu command contract (shobjidl_core.h).</summary>
[GeneratedComInterface, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
internal partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(nint items, out nint name);
    [PreserveSig] int GetIcon(nint items, out nint icon);
    [PreserveSig] int GetToolTip(nint items, out nint tip);
    [PreserveSig] int GetCanonicalName(out Guid name);
    [PreserveSig] int GetState(nint items, int okToBeSlow, out uint state);
    [PreserveSig] int Invoke(nint items, nint bindContext);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out nint commands);
}

[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid riid, out nint instance);
    [PreserveSig] int LockServer(int doLock);
}

/// <summary>
/// The two shell calls the command needs, made straight through the vtables (IShellItemArray
/// GetCount and GetItemAt, IShellItem GetDisplayName) to keep the DLL tiny.
/// </summary>
internal static unsafe class ShellItems
{
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    public static List<string> Paths(nint array)
    {
        var paths = new List<string>();
        if (array == 0) return paths;
        var arrayVtbl = *(nint**)array;
        uint count;
        if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)arrayVtbl[7])(array, &count) != 0) return paths;

        for (uint i = 0; i < count; i++)
        {
            nint item;
            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)arrayVtbl[8])(array, i, &item) != 0 || item == 0) continue;
            try
            {
                var itemVtbl = *(nint**)item;
                nint name;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)itemVtbl[5])(item, SIGDN_FILESYSPATH, &name) == 0 && name != 0)
                {
                    paths.Add(Marshal.PtrToStringUni(name)!);
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)item)[2])(item); // Release
            }
        }
        return paths;
    }
}
