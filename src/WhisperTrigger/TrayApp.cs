using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WhisperTrigger;

sealed class TrayApp : ApplicationContext
{
    private enum Cmd { Toggle, PttDown, PttUp, AutoStop, ContextLost }

    private readonly BlockingCollection<Cmd> _queue = new(new ConcurrentQueue<Cmd>());
    private readonly SynchronizationContext _sync;
    private readonly TypeWhisperClient _client;
    private readonly MouseHook _hook;
    private readonly WindowGuard _guard;
    private readonly SinkWindow _sink;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startAtLoginItem;
    private readonly ToolStripMenuItem _enabledItem;
    private readonly ToolStripMenuItem _toggleItem;
    private Icon _icon;
    private volatile bool _recording; // written on worker thread, read on both
    private string? _savedClipboard;  // worker-thread only
    private System.Threading.Timer? _recordingTimeout; // worker-thread only
    private readonly CancellationTokenSource _startupCts = new();

    private const int RecordingTimeoutMinutes = 5;

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName    = "WhisperTrigger";

    public TrayApp()
    {
        _sync   = SynchronizationContext.Current ?? throw new InvalidOperationException("No sync context.");
        _client = new TypeWhisperClient();

        // Context menu
        _statusItem  = new ToolStripMenuItem("● Idle") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enable mouse buttons", null, OnToggleEnabled) { Checked = true };
        _startAtLoginItem = new ToolStripMenuItem("Start at login", null, OnToggleStartAtLogin)
            { Checked = IsStartAtLoginEnabled() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        _toggleItem = new ToolStripMenuItem("Toggle dictation", null,
            (_, _) => _queue.TryAdd(Cmd.Toggle));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startAtLoginItem);
        menu.Items.Add(new ToolStripMenuItem("About", null, OnAbout));
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

        // Mouse hook — events fire on the message-pump thread; just enqueue
        _hook = new MouseHook();
        _hook.ToggleDown += (_, _) => _queue.TryAdd(Cmd.Toggle);
        _hook.PttDown    += (_, _) => _queue.TryAdd(Cmd.PttDown);
        _hook.PttUp      += (_, _) => _queue.TryAdd(Cmd.PttUp);

        // Focus guard — captures the target window at start; if focus leaves it during
        // recording, we restore the original window before stopping (or, if it's gone,
        // redirect the paste into our own sink window instead of a random app).
        _sink  = new SinkWindow();
        _guard = new WindowGuard { SinkHandle = _sink.Handle };
        _guard.ContextChanged += OnContextChanged;

        // Worker thread handles all HTTP so the hook callback returns instantly
        new Thread(WorkerLoop) { IsBackground = true, Name = "tw-worker" }.Start();

        // Check TypeWhisper API reachability after a short settle delay 
        var cts = _startupCts;
        Task.Run(async () => {
            try
            {
                try { _client.Stop(); } catch { } // clean up recording state left by a previous crash
                await Task.Delay(2500, cts.Token);
                if (!_client.IsReachable())
                    _sync.Post(_ => Notify(
                        "TypeWhisper API not detected.\nEnable it: TypeWhisper → Settings → Advanced → API Server",
                        ToolTipIcon.Warning), null);
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
                                $"Recording auto-stopped after {RecordingTimeoutMinutes} minutes.",
                                ToolTipIcon.Warning), null);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _savedClipboard = null;
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
                TimeSpan.FromMinutes(RecordingTimeoutMinutes),
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
        _client.WaitForIdle(timeoutMs: 10_000);
        string? toRestore = _savedClipboard;
        _savedClipboard = null;
        if (toRestore is not null)
            _sync.Post(_ => {
                try { Clipboard.SetText(toRestore); }
                catch { }
            }, null);
    }

    // ---- UI updates (main thread) ----------------------------------------------

    private void ApplyState(bool recording)
    {
        bool enabled = _hook.Enabled;
        var old = _icon;
        _icon = enabled
            ? MakeIcon(recording ? Color.FromArgb(210, 40, 40) : Color.FromArgb(140, 140, 140), filled: true)
            : MakeIcon(Color.FromArgb(140, 140, 140), filled: false);
        _tray.Icon = _icon;
        old.Dispose();
        string state = !enabled ? "Disabled" : recording ? "Recording..." : "Idle";
        _tray.Text          = $"Whisper Trigger — {state}";
        _statusItem.Text    = !enabled ? "○ Disabled" : recording ? "● Recording..." : "● Idle";
        _toggleItem.Enabled = enabled;
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
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string ver = v is null ? "unknown" : $"{v.Major}.{v.Minor}.{v.Build}";
        Notify($"Whisper Trigger v{ver}", ToolTipIcon.Info);
    }

    private static bool IsStartAtLoginEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AppName) is string;
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
            _queue.CompleteAdding();
            _recordingTimeout?.Dispose();
            _hook.Dispose();
            _guard.Dispose();
            _sink.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _icon.Dispose();
            _client.Dispose();
        }
        base.Dispose(disposing);
    }
}
