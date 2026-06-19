using Velopack;

namespace WhisperTrigger;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "WhisperTrigger_SingleInstance", out bool isFirst);
        if (!isFirst) return; // another instance is already running

        VelopackApp.Build().Run();

        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        using var app = new TrayApp();
        Application.Run(app);
    }
}
