using System.Diagnostics;

namespace Swivel.App.Shell;

/// <summary>
/// Installer hooks. Install leaves nothing outside the user's profile; uninstall removes every trace
/// Swivel added to Windows: the classic menu verb, the Run key and the Windows 11 menu package.
/// </summary>
public static class Installer
{
    private const string ShellPackageName = "Swivel.Shell";
    private const string SignedPackage = "Swivel.Shell.msix";

    public static void AfterInstallOrUpdate()
    {
        // The Windows 11 top-level menu needs a signed sparse package (see build/pack.ps1). Unsigned
        // builds rely on the classic verb, which Swivel registers on its first normal start.
        string msix = Path.Combine(AppContext.BaseDirectory, SignedPackage);
        if (Environment.OSVersion.Version.Build >= 22000 && File.Exists(msix))
            PowerShell($"Add-AppxPackage -Path '{msix}' -ExternalLocation '{AppContext.BaseDirectory.TrimEnd('\\')}' -ForceUpdateFromAnyVersion");
    }

    public static void BeforeUninstall()
    {
        ExplorerMenu.Apply(false, "");
        SignIn.Apply(false);
        PowerShell($"Get-AppxPackage -Name {ShellPackageName} | Remove-AppxPackage");
    }

    private static void PowerShell(string command)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(20_000);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // PowerShell missing or blocked: the classic verb still works, nothing else depends on this.
        }
    }
}
