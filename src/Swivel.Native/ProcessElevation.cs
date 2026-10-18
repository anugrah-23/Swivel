using System.Runtime.InteropServices;

namespace Swivel.Native;

/// <summary>Detects drags coming from elevated windows, which UIPI blocks from reaching Swivel.</summary>
public static partial class ProcessElevation
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    public static bool IsForegroundElevatedAboveUs()
    {
        if (Environment.IsPrivilegedProcess) return false;
        Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out uint pid);
        return pid != 0 && IsElevated(pid);
    }

    public static bool IsElevated(uint pid)
    {
        nint process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return false;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out nint token)) return false;
            try
            {
                return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) && elevated != 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
