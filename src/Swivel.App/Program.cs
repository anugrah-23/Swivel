using Swivel.App.Shell;
using Velopack;

namespace Swivel.App;

public static class Program
{
    /// <summary>
    /// Velopack's hooks run first: on install, update and uninstall it starts Swivel with a hook
    /// argument, the callback runs, and the process exits before any UI appears.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Installer.AfterInstallOrUpdate())
            .OnAfterUpdateFastCallback(_ => Installer.AfterInstallOrUpdate())
            .OnBeforeUninstallFastCallback(_ => Installer.BeforeUninstall())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
