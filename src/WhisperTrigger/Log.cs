using System.Diagnostics;

namespace WhisperTrigger;

// Tiny append-only diagnostic log at %LOCALAPPDATA%\WhisperTrigger\log.txt. Used to capture
// the events around the intermittent "side buttons stopped working" issue (resets, session
// switches, errors) so a recurrence leaves evidence instead of guesswork.
//
// Rotation keeps disk use bounded: when log.txt reaches MaxBytes it rolls to log.1.txt
// (replacing the previous roll), so at most two files of MaxBytes ever exist (~1 MB total).
// Logging must never throw.
static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WhisperTrigger");
    private static string FilePath    => Path.Combine(Dir, "log.txt");
    private static string RolledPath  => Path.Combine(Dir, "log.1.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                string path = FilePath;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    // Roll: log.txt → log.1.txt (overwriting the old roll), then start fresh.
                    try { if (File.Exists(RolledPath)) File.Delete(RolledPath); File.Move(path, RolledPath); }
                    catch { try { File.Delete(path); } catch { } } // if move fails, just reset
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics are best-effort — never let logging break the app */ }
    }

    public static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Process.Start(new ProcessStartInfo(Dir) { UseShellExecute = true });
        }
        catch { }
    }
}
