using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperTrigger;

// Which physical mouse side button drives an action. Kept deliberately small —
// the low-level hook only distinguishes the two X (side) buttons.
enum MouseButtonId { None, XButton1, XButton2 }

// What a dictation button press does while TypeWhisper is asleep (model unloaded) or not
// running.
enum WakeAction
{
    Ask,           // prompt first; wake only if confirmed
    Wake,          // wake it, then wait for another press to record
    WakeAndRecord, // wake it and start recording as soon as it's ready
}

// User-configurable settings, persisted as JSON in %APPDATA%\WhisperTrigger\settings.json.
// Loading is tolerant: a missing or corrupt file yields defaults rather than throwing.
sealed class Settings
{
    public MouseButtonId ToggleButton { get; set; } = MouseButtonId.XButton2; // forward → toggle
    public MouseButtonId PttButton    { get; set; } = MouseButtonId.XButton1; // back → push-to-talk
    public int  AutoStopMinutes { get; set; } = 5;
    public bool AutoUpdate      { get; set; } = false; // install updates automatically on launch

    // Watch for focus leaving the target window mid-recording and, when it does, restore
    // that window and stop dictation early so the text pastes where dictation started.
    // Off means recording runs uninterrupted until the user stops it.
    public bool GuardFocusChanges { get; set; } = true;

    // Idle sleep: free TypeWhisper's memory when dictation hasn't been used for a while.
    // Two tiers — unload the speech model first (wakes in ~3 s), then quit TypeWhisper
    // entirely (wakes in ~5 s). Either timer at 0 skips that tier.
    public bool AutoSleep          { get; set; } = false;
    public int  SleepModelMinutes  { get; set; } = 15;
    public int  QuitAppMinutes     { get; set; } = 60;
    public WakeAction PressWhenAsleep { get; set; } = WakeAction.WakeAndRecord;
    // Also unload the model as soon as the PC locks (part of AutoSleep).
    public bool SleepOnLock { get; set; } = true;

    // Persisted "Enable mouse buttons" tray toggle, so it survives a restart.
    public bool ButtonsEnabled { get; set; } = true;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WhisperTrigger", "settings.json");

    public static Settings Load()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOpts);
                if (loaded is not null) return loaded.Sanitized();
            }
        }
        catch { /* fall through to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* non-fatal: settings just won't persist this time */ }
    }

    // Clamp/repair out-of-range values from a hand-edited or older file.
    private Settings Sanitized()
    {
        if (AutoStopMinutes is < 1 or > 60) AutoStopMinutes = 5;
        if (SleepModelMinutes is < 0 or > 1440) SleepModelMinutes = 15;
        if (QuitAppMinutes is < 0 or > 1440) QuitAppMinutes = 60;
        if (!Enum.IsDefined(PressWhenAsleep)) PressWhenAsleep = WakeAction.WakeAndRecord;
        // Both actions on the same button is ambiguous — drop PTT so toggle wins.
        if (ToggleButton != MouseButtonId.None && ToggleButton == PttButton)
            PttButton = MouseButtonId.None;
        return this;
    }
}
