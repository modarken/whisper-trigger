using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WhisperTrigger;

// Finds, launches and quits the TypeWhisper desktop app itself (as opposed to
// TypeWhisperClient, which talks to its HTTP API). Used by the idle-sleep feature:
// quit TypeWhisper after a long idle to free its memory, relaunch it on demand.
//
// TypeWhisper ships two ways:
//   - Store / MSIX package (current): launched by its AppUserModelID through the
//     ApplicationActivationManager, which also lets us pass --minimized.
//   - Velopack install (older): %LOCALAPPDATA%\TypeWhisper\current\TypeWhisper.exe.
// Located once at startup and re-located lazily if it's missing (e.g. installed later).
sealed class TypeWhisperApp
{
    private const string ProcessName = "TypeWhisper";
    private const string LaunchArgs  = "--minimized"; // start to tray, no window

    private string? _aumid;      // MSIX: "<PackageFamilyName>!<AppId>"
    private string? _exePath;    // Velopack-style install
    private string? _installDir; // to recognise TypeWhisper's own processes

    public TypeWhisperApp() => Locate();

    // True if we know how to launch TypeWhisper.
    public bool CanLaunch => _aumid is not null || _exePath is not null;

    // Installed in any form we recognise (including a bare uninstall-registry entry,
    // which is enough to say "installed" but not enough to launch it).
    public bool IsInstalled => CanLaunch || HasUninstallEntry();

    public bool IsRunning
    {
        get
        {
            var procs = Find();
            try { return procs.Length > 0; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    // Seconds since the oldest TypeWhisper process started, or null if none is running.
    // Lets "not answering" be told apart from "still starting up".
    public double? RunningForSeconds
    {
        get
        {
            var procs = Find();
            try
            {
                var starts = procs.Select(p => { try { return (DateTime?)p.StartTime; } catch { return null; } })
                                  .Where(t => t is not null).ToList();
                return starts.Count == 0 ? (procs.Length > 0 ? double.MaxValue : null)
                                         : (DateTime.Now - starts.Min()!.Value).TotalSeconds;
            }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    // Whether TypeWhisper has a visible window open (its settings/dashboard). We never
    // auto-quit then: the user is actively looking at it.
    public bool HasVisibleWindow
    {
        get
        {
            var procs = Find();
            try { return procs.Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } }); }
            finally { foreach (var p in procs) p.Dispose(); }
        }
    }

    // Starts TypeWhisper minimized to the tray. Returns false if we can't (not found).
    public bool Launch()
    {
        if (!CanLaunch) Locate();
        try
        {
            if (_aumid is not null)
            {
                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                int hr = mgr.ActivateApplication(_aumid, LaunchArgs, 0, out uint pid);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                Log.Write($"launched TypeWhisper ({_aumid}) pid {pid}");
                return true;
            }
            if (_exePath is not null)
            {
                using var p = Process.Start(new ProcessStartInfo(_exePath, LaunchArgs) { UseShellExecute = false });
                Log.Write($"launched TypeWhisper ({_exePath}) pid {p?.Id}");
                return true;
            }
        }
        catch (Exception ex) { Log.Write($"TypeWhisper launch failed: {ex.Message}"); }
        return false;
    }

    // Opens TypeWhisper's window on its Home page (recent transcriptions + "View all
    // history"). There's no switch for the History window itself; --dashboard is the
    // closest. Passed to the running instance if there is one, else it starts TypeWhisper.
    public bool OpenDashboard()
    {
        if (!CanLaunch) Locate();
        try
        {
            if (_aumid is not null)
            {
                var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
                int hr = mgr.ActivateApplication(_aumid, "--dashboard", 0, out _);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                return true;
            }
            if (_exePath is not null)
            {
                using var p = Process.Start(new ProcessStartInfo(_exePath, "--dashboard") { UseShellExecute = false });
                return true;
            }
        }
        catch (Exception ex) { Log.Write($"open TypeWhisper dashboard failed: {ex.Message}"); }
        return false;
    }

    // Closes TypeWhisper. Its tray app ignores a polite WM_CLOSE (it hides instead of
    // exiting), so this terminates the process; its transcription worker watches the
    // parent and exits with it. Callers only do this while dictation is idle.
    public bool Quit()
    {
        var procs = Find();
        try
        {
            if (procs.Length == 0) return true;
            foreach (var p in procs)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            foreach (var p in procs)
            {
                try { p.WaitForExit(5000); } catch { }
            }
            bool gone = procs.All(p => { try { return p.HasExited; } catch { return true; } });
            Log.Write($"quit TypeWhisper ({procs.Length} process(es)) -> {(gone ? "ok" : "still running")}");
            return gone;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    // TypeWhisper processes — matched by name, and by install folder when we know it, so an
    // unrelated program that happens to share the name is never touched.
    private Process[] Find()
    {
        var all = Process.GetProcessesByName(ProcessName);
        if (_installDir is null) return all;
        var mine = new List<Process>();
        foreach (var p in all)
        {
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* access denied → can't verify */ }
            if (path is null || path.StartsWith(_installDir, StringComparison.OrdinalIgnoreCase))
                mine.Add(p);
            else
                p.Dispose();
        }
        return mine.ToArray();
    }

    private void Locate()
    {
        // MSIX: the per-user AppModel repository lists installed packages by full name
        // ("<Name>_<Version>_<Arch>__<PublisherId>"); its subkeys are the app IDs.
        const string repo = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(repo);
            if (key is not null)
            {
                foreach (var full in key.GetSubKeyNames()
                             .Where(n => n.StartsWith("TypeWhisper.", StringComparison.OrdinalIgnoreCase))
                             .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                {
                    using var pkg = key.OpenSubKey(full);
                    string? root   = pkg?.GetValue("PackageRootFolder") as string;
                    string? appId  = pkg?.GetSubKeyNames().FirstOrDefault();
                    string[] parts = full.Split('_');
                    if (appId is null || root is null || !Directory.Exists(root) || parts.Length < 5) continue;
                    // Family name = Name + "_" + PublisherId (first and last segments).
                    _aumid      = $"{parts[0]}_{parts[^1]}!{appId}";
                    _installDir = root;
                    _exePath    = null;
                    return;
                }
            }
        }
        catch { }

        string exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TypeWhisper", "current", "TypeWhisper.exe");
        if (File.Exists(exe))
        {
            _exePath    = exe;
            _installDir = Path.GetDirectoryName(exe);
        }
    }

    private static bool HasUninstallEntry()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var root in roots)
        {
            try
            {
                using var key = hive.OpenSubKey(root);
                if (key is null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    using var entry = key.OpenSubKey(sub);
                    if (entry?.GetValue("DisplayName") is string name &&
                        name.Contains("TypeWhisper", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
        }
        return false;
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr a, IntPtr b, IntPtr c, out uint d);
        [PreserveSig] int ActivateForProtocol(IntPtr a, IntPtr b, out uint c);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }
}
