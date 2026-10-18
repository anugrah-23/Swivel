using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Text.Json;

namespace Swivel.ShellExt;

/// <summary>
/// "Spin with Swivel" on Windows 11's top-level menu. It only hands the selection to Swivel, over
/// the same pipe the classic menu's <c>Swivel.exe --wheel</c> uses, and starts Swivel if needed.
/// </summary>
[GeneratedComClass]
internal sealed partial class SwivelCommand : IExplorerCommand
{
    private const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), E_FAIL = unchecked((int)0x80004005);

    public int GetTitle(nint items, out nint name)
    {
        name = Marshal.StringToCoTaskMemUni("Spin with Swivel");
        return S_OK;
    }

    public int GetIcon(nint items, out nint icon)
    {
        icon = Marshal.StringToCoTaskMemUni($"\"{Exports.SwivelExe}\",0");
        return S_OK;
    }

    public int GetToolTip(nint items, out nint tip)
    {
        tip = 0;
        return E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid name)
    {
        name = Exports.Clsid;
        return S_OK;
    }

    public int GetState(nint items, int okToBeSlow, out uint state)
    {
        state = 0; // ECS_ENABLED
        return S_OK;
    }

    public int Invoke(nint items, nint bindContext)
    {
        try
        {
            var paths = ShellItems.Paths(items);
            if (paths.Count == 0) return S_OK;
            if (!SendToRunningSwivel(paths)) StartSwivel(paths);
            return S_OK;
        }
        catch
        {
            return E_FAIL;
        }
    }

    public int GetFlags(out uint flags)
    {
        flags = 0; // ECF_DEFAULT
        return S_OK;
    }

    public int EnumSubCommands(out nint commands)
    {
        commands = 0;
        return E_NOTIMPL;
    }

    private static bool SendToRunningSwivel(List<string> paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", $"Swivel-{Environment.UserName}-wheel", PipeDirection.Out);
            pipe.Connect(300);
            var json = new MemoryStream();
            using (var writer = new Utf8JsonWriter(json))
            {
                writer.WriteStartArray();
                foreach (var path in paths) writer.WriteStringValue(path);
                writer.WriteEndArray();
            }
            json.WriteByte((byte)'\n');
            pipe.Write(json.GetBuffer(), 0, (int)json.Length);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void StartSwivel(List<string> paths)
    {
        var start = new ProcessStartInfo(Exports.SwivelExe) { UseShellExecute = false };
        start.ArgumentList.Add("--wheel");
        foreach (var path in paths) start.ArgumentList.Add(path);
        Process.Start(start)?.Dispose();
    }
}

[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(nint outer, in Guid riid, out nint instance)
    {
        instance = 0;
        if (outer != 0) return CLASS_E_NOAGGREGATION;
        return Exports.QueryFor(new SwivelCommand(), riid, out instance);
    }

    public int LockServer(int doLock) => 0;
}
