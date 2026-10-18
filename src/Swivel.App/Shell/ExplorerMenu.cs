using Microsoft.Win32;
using Serilog;

namespace Swivel.App.Shell;

/// <summary>
/// The classic right-click verb, per user with no admin rights: Windows 10's menu, and "Show more
/// options" on Windows 11. Windows 11's top-level entry comes from the sparse package in Swivel.ShellExt.
/// </summary>
public static class ExplorerMenu
{
    private const string Verb = "Swivel";
    private static readonly string[] Targets = [@"Software\Classes\*\shell", @"Software\Classes\Directory\shell"];

    public static void Apply(bool enabled, string exePath)
    {
        try
        {
            foreach (string target in Targets)
            {
                if (!enabled)
                {
                    Registry.CurrentUser.DeleteSubKeyTree($@"{target}\{Verb}", throwOnMissingSubKey: false);
                    continue;
                }
                using var key = Registry.CurrentUser.CreateSubKey($@"{target}\{Verb}");
                key.SetValue("MUIVerb", "Spin with Swivel");
                key.SetValue("Icon", $"\"{exePath}\",0");
                key.SetValue("MultiSelectModel", "Player");
                using var command = key.CreateSubKey("command");
                command.SetValue(null, $"\"{exePath}\" --wheel \"%1\"");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Couldn't update the Explorer menu");
        }
    }
}
