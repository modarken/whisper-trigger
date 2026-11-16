using Velopack;

namespace WhisperTrigger;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Must run first: when Velopack launches the exe for an install/update/uninstall
        // hook it handles it here and exits. Behind the single-instance check, those hooks
        // were skipped whenever the app was already running.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => TrayApp.RemoveStartAtLogin())
            .Run();

        using var mutex = new Mutex(true, "WhisperTrigger_SingleInstance", out bool isFirst);
        if (!isFirst) return; // another instance is already running

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var app = new TrayApp();
        Application.Run(app);
    }
}
