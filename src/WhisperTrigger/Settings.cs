using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisperTrigger;

// Which physical mouse side button drives an action. Kept deliberately small —
// the low-level hook only distinguishes the two X (side) buttons.
enum MouseButtonId { None, XButton1, XButton2 }

// User-configurable settings, persisted as JSON in %APPDATA%\WhisperTrigger\settings.json.
// Loading is tolerant: a missing or corrupt file yields defaults rather than throwing.
sealed class Settings
{
    public MouseButtonId ToggleButton { get; set; } = MouseButtonId.XButton2; // forward → toggle
    public MouseButtonId PttButton    { get; set; } = MouseButtonId.XButton1; // back → push-to-talk
    public int  AutoStopMinutes { get; set; } = 5;
    public bool AutoUpdate      { get; set; } = false; // install updates automatically on launch

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
        // Both actions on the same button is ambiguous — drop PTT so toggle wins.
        if (ToggleButton != MouseButtonId.None && ToggleButton == PttButton)
            PttButton = MouseButtonId.None;
        return this;
    }
}
