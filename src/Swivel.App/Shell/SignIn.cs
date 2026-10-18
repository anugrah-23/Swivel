using Microsoft.Win32;
using Serilog;

namespace Swivel.App.Shell;

/// <summary>Launch at sign-in through the per-user Run key (no admin, visible in Task Manager's Startup tab).</summary>
public static class SignIn
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Swivel";

    /// <summary>
    /// The exe to start. An installed Swivel runs from "current\" under Velopack's root, whose stub
    /// Swivel.exe survives updates, so the stub is preferred when present.
    /// </summary>
    public static string LauncherPath
    {
        get
        {
            string exe = Environment.ProcessPath!;
            string? folder = Path.GetDirectoryName(exe);
            if (folder != null && Path.GetFileName(folder).Equals("current", StringComparison.OrdinalIgnoreCase))
            {
                string stub = Path.Combine(Path.GetDirectoryName(folder)!, "Swivel.exe");
                if (File.Exists(stub)) return stub;
            }
            return exe;
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Name, $"\"{LauncherPath}\" --background");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Couldn't update launch at sign-in");
        }
    }
}
