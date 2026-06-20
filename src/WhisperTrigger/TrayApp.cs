using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Velopack;

namespace WhisperTrigger;

sealed class TrayApp : ApplicationContext
{
    private enum Cmd { Toggle, PttDown, PttUp, AutoStop, ContextLost, Reset }

    private readonly BlockingCollection<Cmd> _queue = new(new ConcurrentQueue<Cmd>());
    private readonly SynchronizationContext _sync;
    private readonly TypeWhisperClient _client;
    private readonly MouseHook _hook;
    private readonly WindowGuard _guard;
    private readonly SinkWindow _sink;
    private readonly OverlayWindow _overlay;
    private readonly Updater _updater = new();
    private readonly Settings _settings;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _startAtLoginItem;
    private readonly ToolStripMenuItem _checkUpdatesItem;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _toggleItem;
    private Icon _icon;
    private volatile bool _recording; // written on worker thread, read on both
    private string? _savedClipboard;  // worker-thread only
    private System.Threading.Timer? _recordingTimeout; // worker-thread only
    private System.Threading.Timer? _healthTimer;      // periodic TypeWhisper reachability poll
    private volatile bool _apiReachable = true;        // last heartbeat result
    private volatile bool _apiKnown;                   // false until the first heartbeat completes
    private readonly bool _twInstalled = IsTypeWhisperInstalled(); // checked once at startup
    private int _healthBusy;                            // 0/1 guard so heartbeats don't overlap
    private readonly CancellationTokenSource _startupCts = new();
    private UpdateInfo? _pendingUpdate;          // set once an update is downloaded & ready

    private const int HealthIntervalMs = 12_000;

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName    = "WhisperTrigger";

    public TrayApp()
    {
        _sync   = SynchronizationContext.Current ?? throw new InvalidOperationException("No sync context.");
        _client = new TypeWhisperClient();
        _settings = Settings.Load();
        Log.Write($"app started v{CurrentVersion()}");

        // Context menu
        _statusItem  = new ToolStripMenuItem("● Idle") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enable mouse buttons", null, OnToggleEnabled) { Checked = true };
        _settingsItem = new ToolStripMenuItem("Settings…", null, OnOpenSettings);
        _startAtLoginItem = new ToolStripMenuItem("Start at login", null, OnToggleStartAtLogin)
            { Checked = IsStartAtLoginEnabled() };
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates", null, OnCheckForUpdates);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        _toggleItem = new ToolStripMenuItem("Toggle dictation", null,
            (_, _) => _queue.TryAdd(Cmd.Toggle));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Reset (re-arm buttons)", null, OnReset));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_startAtLoginItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("About", null, OnAbout));
        menu.Items.Add(new ToolStripMenuItem("Open logs folder", null, (_, _) => Log.OpenFolder()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        // Tray icon
        _icon = MakeIcon(Color.FromArgb(140, 140, 140), filled: true);
        _tray = new NotifyIcon
        {
            Icon             = _icon,
            Text             = "Whisper Trigger — Idle",
            ContextMenuStrip = menu,
            Visible          = true,
        };
        _tray.DoubleClick += (_, _) => _queue.TryAdd(Cmd.Toggle);
        _tray.BalloonTipClicked += OnBalloonClicked; // click an "update ready" toast to install

        // Mouse hook — events fire on the message-pump thread; just enqueue
        _hook = new MouseHook
        {
            ToggleButton = _settings.ToggleButton,
            PttButton    = _settings.PttButton,
        };
        _hook.ToggleDown += (_, _) => _queue.TryAdd(Cmd.Toggle);
        _hook.PttDown    += (_, _) => _queue.TryAdd(Cmd.PttDown);
        _hook.PttUp      += (_, _) => _queue.TryAdd(Cmd.PttUp);

        // Recording badge — visible over full-screen RDP where the tray icon is hidden.
        _overlay = new OverlayWindow();
        _ = _overlay.Handle; // force handle creation so the guard can ignore it

        // Focus guard — captures the target window at start; if focus leaves it during
        // recording, we restore the original window before stopping (or, if it's gone,
        // redirect the paste into our own sink window instead of a random app).
        _sink  = new SinkWindow();
        _guard = new WindowGuard { SinkHandle = _sink.Handle, OverlayHandle = _overlay.Handle };
        _guard.ContextChanged += OnContextChanged;

        // Worker thread handles all HTTP so the hook callback returns instantly
        new Thread(WorkerLoop) { IsBackground = true, Name = "tw-worker" }.Start();

        // Heartbeat: poll TypeWhisper's API so the tray reflects when it goes down,
        // not only when a button press fails. First beat after a short settle delay.
        _healthTimer = new System.Threading.Timer(HealthCheck, null, 2500, HealthIntervalMs);

        // Windows can silently drop a low-level mouse hook (lock/unlock, RDP reconnect),
        // which is the suspected cause of "buttons stopped working until I restarted it."
        // We don't auto-heal — but we log session changes so the log shows what happened
        // right before a failure, and the Reset menu item re-arms the hook on demand.
        SystemEvents.SessionSwitch += OnSessionSwitch;

        var cts = _startupCts;
        Task.Run(async () => {
            try
            {
                try { _client.Stop(); } catch { } // clean up recording state left by a previous crash
                await Task.Delay(2500, cts.Token);
                if (_settings.AutoUpdate)
                    await AutoUpdateOnStartup(cts.Token);
            }
            catch (OperationCanceledException) { }
        });
    }

    // ---- Worker thread ---------------------------------------------------------

    private void WorkerLoop()
    {
        // GetConsumingEnumerable throws ObjectDisposedException when CompleteAdding races with Dispose.
        try { WorkerLoopInner(); } catch (ObjectDisposedException) { }
    }

    private void WorkerLoopInner()
    {
        foreach (var cmd in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (cmd)
                {
                    case Cmd.Toggle:
                        bool isRecording = _client.QueryRecording() ?? _recording;
                        if (isRecording) { DoStop(); WaitThenRestoreClipboard(); }
                        else             { CaptureClipboard(); DoStart(); }
                        break;
                    case Cmd.PttDown:
                        CaptureClipboard();
                        DoStart();
                        break;
                    case Cmd.PttUp:
                        // May already be stopped if a context change redirected the paste.
                        if (_recording) { DoStop(); WaitThenRestoreClipboard(); }
                        break;
                    case Cmd.ContextLost:
                        // Focus left the target window; OnContextChanged has already put
                        // focus on the original window (or the sink), so this stop pastes
                        // the transcription there rather than into a random app.
                        if (_recording)
                        {
                            DoStop();
                            WaitThenRestoreClipboard();
                        }
                        break;
                    case Cmd.AutoStop:
                        if (_recording)
                        {
                            DoStop();
                            WaitThenRestoreClipboard();
                            _sync.Post(_ => Notify(
                                $"Recording auto-stopped after {_settings.AutoStopMinutes} minutes.",
                                ToolTipIcon.Warning), null);
                        }
                        break;
                    case Cmd.Reset:
                        // Clear any recording state TypeWhisper or we might still think is on,
                        // returning to a clean idle state without a process restart.
                        try { _client.Stop(); } catch { }
                        _guard.Disarm();
                        _recordingTimeout?.Dispose();
                        _recordingTimeout = null;
                        _savedClipboard = null;
                        _recording = false;
                        _sync.Post(_ => ApplyState(false), null);
                        break;
                }
            }
            catch (Exception ex)
            {
                _savedClipboard = null;
                Log.Write($"worker error on {cmd}: {ex}");
                _sync.Post(_ => Notify(ex.Message, ToolTipIcon.Error), null);
            }
        }
    }

    // Fires on a thread-pool (debounce timer) thread when focus leaves the target window
    // during recording. Bring the original window back to the foreground so the paste
    // lands there; only if it's gone (closed/crashed) fall back to the sink window. Done
    // synchronously so focus is settled before the worker stops dictation.
    private void OnContextChanged(object? sender, EventArgs e)
    {
        if (!_recording) return;
        bool restored = false;
        _sync.Send(_ => {
            restored = _guard.RestoreTarget();
            if (!restored) _sink.ShowAndFocus();
        }, null);
        _queue.TryAdd(Cmd.ContextLost);
        _sync.Post(_ => Notify(
            restored
                ? "Focus changed — returned to your original window."
                : "Original window unavailable — dictation captured in a safe window.",
            ToolTipIcon.Warning), null);
    }

    private void DoStart()
    {
        if (_client.Start())
        {
            _recording = true;
            _guard.CaptureTarget();
            _sync.Post(_ => ApplyState(true), null);
            _recordingTimeout?.Dispose();
            _recordingTimeout = new System.Threading.Timer(
                _ => _queue.TryAdd(Cmd.AutoStop),
                null,
                TimeSpan.FromMinutes(_settings.AutoStopMinutes),
                Timeout.InfiniteTimeSpan);
        }
        else
        {
            _sync.Post(_ => Notify("Could not start recording.", ToolTipIcon.Error), null);
        }
    }

    private void DoStop()
    {
        _guard.Disarm();
        _recordingTimeout?.Dispose();
        _recordingTimeout = null;
        bool confirmed = _client.Stop();
        _recording = false;
        _sync.Post(_ => {
            ApplyState(false);
            if (!confirmed)
                Notify("Stop command was not confirmed — TypeWhisper may be out of sync.", ToolTipIcon.Warning);
        }, null);
    }

    private void CaptureClipboard()
    {
        string? saved = null;
        bool failed = false;
        _sync.Send(_ => {
            try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); }
            catch { failed = true; }
        }, null);
        _savedClipboard = saved;
        if (failed)
            _sync.Post(_ => Notify(
                "Could not read clipboard — it will not be restored after dictation.",
                ToolTipIcon.Warning), null);
    }

    private void WaitThenRestoreClipboard()
    {
        string? toRestore = _savedClipboard;
        _savedClipboard = null;
        if (toRestore is null) return; // nothing saved → nothing we could clobber

        // TypeWhisper transcribes *after* recording stops, then pastes by putting the
        // transcription on the clipboard and sending Ctrl+V. Restoring the old clipboard
        // too early clobbers that transcription, so TypeWhisper pastes our old text instead
        // — the "it pasted what I copied before" bug, worst on long paragraphs that take
        // longer to transcribe. Wait until the clipboard actually changes away from the
        // saved text (that's TypeWhisper writing the transcription), then give the paste
        // time to land before putting the old clipboard back.
        WaitForTranscriptionThenPaste(toRestore);

        _sync.Post(_ => {
            try { Clipboard.SetText(toRestore); }
            catch { }
        }, null);
    }

    // Blocks the worker thread until TypeWhisper has written its transcription to the
    // clipboard (or we give up), then waits a buffer for the Ctrl+V paste to complete.
    // Clipboard reads are marshalled to the UI thread (clipboard access must be STA).
    private void WaitForTranscriptionThenPaste(string original)
    {
        const int ChangeTimeoutMs = 20_000; // long paragraphs can take a while to transcribe
        const int PasteBufferMs   = 1200;   // headroom for Ctrl+V to land (incl. RDP latency)

        var deadline = DateTime.UtcNow.AddMilliseconds(ChangeTimeoutMs);
        bool changed = false;
        while (DateTime.UtcNow < deadline)
        {
            string? current = null;
            _sync.Send(_ => {
                try { current = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
                catch { }
            }, null);
            if (current is not null && current != original) { changed = true; break; }
            Thread.Sleep(150);
        }
        // Saw the transcription land → wait out the paste. Didn't (identical text, or no
        // paste happened) → just a short settle so we don't hang on the old clipboard.
        Thread.Sleep(changed ? PasteBufferMs : 600);
    }

    // Runs on a timer (thread-pool) thread. Pings the API and, on a state change,
    // updates the tray and posts a single notification — never on every beat.
    private void HealthCheck(object? _)
    {
        if (Interlocked.Exchange(ref _healthBusy, 1) == 1) return; // a beat is still in flight
        try
        {
            bool up       = _client.IsReachable();
            bool wasKnown = _apiKnown;
            bool wasUp    = _apiReachable;
            _apiReachable = up;
            _apiKnown     = true;

            if (!wasKnown)
            {
                if (!up) _sync.Post(_ => Notify(
                    _twInstalled
                        ? "TypeWhisper isn't responding.\nIs it running, with Settings → Advanced → API Server enabled?"
                        : "TypeWhisper doesn't appear to be installed.\nGet it at https://www.typewhisper.com",
                    ToolTipIcon.Warning), null);
            }
            else if (wasUp && !up)
                _sync.Post(_ => Notify("TypeWhisper stopped responding — is it still running?",
                    ToolTipIcon.Warning), null);
            else if (!wasUp && up)
                _sync.Post(_ => Notify("TypeWhisper reconnected.", ToolTipIcon.Info), null);

            if (!wasKnown || wasUp != up)
                _sync.Post(_ => ApplyState(_recording), null); // refresh icon/status
        }
        catch { /* heartbeat is best-effort */ }
        finally { Interlocked.Exchange(ref _healthBusy, 0); }
    }

    // Log-only: records session changes (lock/unlock, RDP connect/disconnect) so the log
    // shows what preceded a "buttons stopped working" event. We deliberately don't act on
    // these — recovery is the user's call via Reset.
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        Log.Write($"session switch: {e.Reason}");

    // Manual recovery (right-click → Reset): re-arm the mouse hook and clear any stuck
    // recording state, so a dropped hook can be fixed without closing and reopening the app.
    private void OnReset(object? sender, EventArgs e)
    {
        // Log how long the hook had been silent first — a large value here is strong
        // evidence the hook really had been dropped (vs. a problem elsewhere).
        Log.Write($"manual reset (hook idle was {_hook.IdleMillis}ms)");
        bool ok = _hook.Reinstall();
        Log.Write($"reset: hook reinstall -> {(ok ? "ok" : "FAILED")}");
        _queue.TryAdd(Cmd.Reset); // worker clears recording/clipboard/timer state
        Notify(ok ? "Reset — mouse buttons re-armed." : "Reset could not re-arm the hook; please restart.",
            ok ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    // ---- UI updates (main thread) ----------------------------------------------

    private void ApplyState(bool recording)
    {
        bool enabled = _hook.Enabled;
        // Surface "TypeWhisper is down" only when idle — recording/disabled take priority.
        bool apiDown = enabled && !recording && _apiKnown && !_apiReachable;

        var gray  = Color.FromArgb(140, 140, 140);
        var red   = Color.FromArgb(210, 40, 40);
        var amber = Color.FromArgb(230, 160, 40);

        var old = _icon;
        _icon = enabled
            ? MakeIcon(recording ? red : apiDown ? amber : gray, filled: true)
            : MakeIcon(gray, filled: false);
        _tray.Icon = _icon;
        old.Dispose();
        string offline = _twInstalled ? "TypeWhisper offline" : "TypeWhisper not installed";
        string state = !enabled ? "Disabled" : recording ? "Recording..." : apiDown ? offline : "Idle";
        _tray.Text          = $"Whisper Trigger — {state}";
        _statusItem.Text    = !enabled ? "○ Disabled" : recording ? "● Recording..." : apiDown ? $"▲ {offline}" : "● Idle";
        _toggleItem.Enabled = enabled;

        if (recording) _overlay.ShowRecording();
        else           _overlay.HideRecording();
    }

    private void Notify(string msg, ToolTipIcon icon = ToolTipIcon.Info) =>
        _tray.ShowBalloonTip(4000, "Whisper Trigger", msg, icon);

    // ---- Menu handlers ---------------------------------------------------------

    private void OnToggleStartAtLogin(object? sender, EventArgs e)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (IsStartAtLoginEnabled())
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            _startAtLoginItem.Checked = false;
        }
        else
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                key.SetValue(AppName, $"\"{exe}\"", RegistryValueKind.String); // explicit String kind required; update checkbox only after confirmed write
                _startAtLoginItem.Checked = true;
            }
        }
    }

    private void OnToggleEnabled(object? sender, EventArgs e)
    {
        _hook.Enabled = !_hook.Enabled;
        _enabledItem.Checked = _hook.Enabled;
        ApplyState(_recording);
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        using var about = new AboutBox(CurrentVersion(), _icon, () => OnCheckForUpdates(this, EventArgs.Empty));
        about.ShowDialog();
    }

    private static string CurrentVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnOpenSettings(object? sender, EventArgs e)
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var r = dlg.Result;
        _settings.ToggleButton    = r.ToggleButton;
        _settings.PttButton       = r.PttButton;
        _settings.AutoStopMinutes = r.AutoStopMinutes;
        _settings.AutoUpdate      = r.AutoUpdate;
        _settings.Save();

        // Apply immediately — no restart needed.
        _hook.ToggleButton   = _settings.ToggleButton;
        _hook.PttButton      = _settings.PttButton;
    }

    // ---- Updates ---------------------------------------------------------------

    // Manual "Check for updates": check, download if found, then invite a click to install.
    private void OnCheckForUpdates(object? sender, EventArgs e)
    {
        if (!_updater.IsInstalled)
        {
            Notify("Updates are only available in the installed version.", ToolTipIcon.Info);
            return;
        }
        Notify("Checking for updates…", ToolTipIcon.Info);
        Task.Run(async () =>
        {
            try
            {
                var info = await _updater.CheckAsync();
                if (info is null)
                {
                    _sync.Post(_ => Notify("You're up to date.", ToolTipIcon.Info), null);
                    return;
                }
                await _updater.DownloadAsync(info);
                _pendingUpdate = info;
                _sync.Post(_ => Notify(
                    $"Update {info.TargetFullRelease.Version} ready — click here to install.",
                    ToolTipIcon.Info), null);
            }
            catch (Exception ex)
            {
                _sync.Post(_ => Notify($"Update check failed: {ex.Message}", ToolTipIcon.Error), null);
            }
        });
    }

    // Silent path used on launch when "Automatically install updates" is on.
    private async Task AutoUpdateOnStartup(CancellationToken token)
    {
        if (!_updater.IsInstalled) return;
        try
        {
            var info = await _updater.CheckAsync();
            if (info is null || token.IsCancellationRequested) return;
            await _updater.DownloadAsync(info);
            _sync.Post(_ => Notify(
                $"Installing update {info.TargetFullRelease.Version} — restarting…", ToolTipIcon.Info), null);
            await Task.Delay(1500, token);
            _updater.ApplyAndRestart(info); // relaunches the app
        }
        catch (OperationCanceledException) { }
        catch { /* auto-update is best-effort; stay on current version */ }
    }

    // Clicking the "update ready" toast installs the downloaded update and restarts.
    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        var pending = _pendingUpdate;
        if (pending is null) return;
        _pendingUpdate = null;
        try { _updater.ApplyAndRestart(pending); }
        catch (Exception ex) { Notify($"Could not install update: {ex.Message}", ToolTipIcon.Error); }
    }

    private static bool IsStartAtLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AppName) is string;
    }

    // Best-effort check for whether TypeWhisper is installed, via the Windows uninstall
    // registry. Used only to report "not installed" vs "not responding" — we never act on
    // it (starting/installing TypeWhisper is not this tool's job).
    private static bool IsTypeWhisperInstalled()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var root in roots)
        {
            using var key = hive.OpenSubKey(root);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var entry = key.OpenSubKey(sub);
                    if (entry?.GetValue("DisplayName") is string name &&
                        name.Contains("TypeWhisper", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { /* skip unreadable entries */ }
            }
        }
        return false;
    }

    // ---- Icon drawing ----------------------------------------------------------

    private static Icon MakeIcon(Color color, bool filled)
    {
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        if (filled)
        {
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 14, 14);
        }
        else
        {
            using var pen = new Pen(color, 1.5f);
            g.DrawEllipse(pen, 2, 2, 12, 12);
        }
        nint handle = bmp.GetHicon();
        try     { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { _ = DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    // ---- Cleanup ---------------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _startupCts.Cancel();
            _startupCts.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _queue.CompleteAdding();
            _healthTimer?.Dispose();
            _recordingTimeout?.Dispose();
            _hook.Dispose();
            _guard.Dispose();
            _overlay.Dispose();
            _sink.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
