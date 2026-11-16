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
        if (!isFirst)
        {
            // Already running (e.g. launched again from the Start menu): open its panel.
            // We were just started by the user, so we may hand it the right to come forward.
            try
            {
                AllowSetForegroundWindow(-1); // ASFW_ANY
                using var show = EventWaitHandle.OpenExisting(TrayApp.ShowPanelEventName);
                show.Set();
            }
            catch { }
            return;
        }

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var app = new TrayApp();
        Application.Run(app);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
