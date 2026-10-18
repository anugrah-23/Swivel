using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Swivel.ShellExt;

/// <summary>The DLL's COM entry points, exported by NativeAOT.</summary>
internal static unsafe partial class Exports
{
    /// <summary>Also referenced by Package\AppxManifest.xml; keep the two in step.</summary>
    public static readonly Guid Clsid = new("6D4B4A7E-3C2B-4E1F-9B7A-5157E1D0C0DE");

    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111), S_FALSE = 1;
    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Lazy<string> ExePath = new(() =>
    {
        const uint FROM_ADDRESS = 0x4, UNCHANGED_REFCOUNT = 0x2;
        delegate* unmanaged<int> self = &DllCanUnloadNow;
        GetModuleHandleExW(FROM_ADDRESS | UNCHANGED_REFCOUNT, (nint)self, out nint module);
        char* buffer = stackalloc char[1024];
        int length = (int)GetModuleFileNameW(module, buffer, 1024);
        string dll = new(buffer, 0, length);
        return Path.Combine(Path.GetDirectoryName(dll)!, "Swivel.exe");
    });

    /// <summary>Swivel.exe, installed beside this DLL.</summary>
    public static string SwivelExe => ExePath.Value;

    public static int QueryFor(object instance, in Guid riid, out nint result)
    {
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try { return Marshal.QueryInterface(unknown, riid, out result); }
        finally { Marshal.Release(unknown); }
    }

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    private static int DllGetClassObject(Guid* clsid, Guid* riid, nint* result)
    {
        *result = 0;
        try
        {
            if (*clsid != Clsid) return CLASS_E_CLASSNOTAVAILABLE;
            int hr = QueryFor(new ClassFactory(), *riid, out nint factory);
            *result = factory;
            return hr;
        }
        catch
        {
            return unchecked((int)0x80004005);
        }
    }

    /// <summary>Stays loaded: unloading a NativeAOT image isn't supported.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    private static int DllCanUnloadNow() => S_FALSE;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetModuleFileNameW(nint module, char* buffer, uint size);
}
