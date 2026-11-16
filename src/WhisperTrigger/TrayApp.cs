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
    private enum Cmd
    {
        Toggle, PttDown, PttUp, AutoStop, ContextLost, Reset,
        Wake, SleepModel, QuitTw, RestartTw, IdleCheck, LockSleep, SyncRecording,
    }

    // What a pre-press health check found (see CheckReadiness).
    private enum Readiness
    {
        Ready,        // answering with a model loaded — dictate
        Asleep,       // we put it to sleep (or its model is unloaded) — a planned wake
        NotRunning,   // closed or crashed — needs starting
        Starting,     // process just launched, API not up yet — wait for it
        Unresponsive, // running for a while but not answering — needs a restart
    }

    // Each command carries the tick it was queued at, so presses made while the worker
    // was stuck (a hanging API call, a wake-up, an "wake it?" prompt) can be recognised
    // as stale and dropped (see WorkerLoopInner).
    private readonly record struct Queued(Cmd Cmd, long Ticks);
    private readonly BlockingCollection<Queued> _queue = new(new ConcurrentQueue<Queued>());
    private long _discardBeforeTicks; // worker-thread only: drop presses queued before this

    // Idle sleep (see Settings.AutoSleep). Awake = normal. ModelAsleep = we unloaded the
    // speech model. Off = we quit TypeWhisper. A press in either sleep state wakes it.
    private enum SleepState { Awake, ModelAsleep, Off }
    private volatile SleepState _sleep = SleepState.Awake; // see SetSleep
    private long _sleepChangedTicks; // when _sleep last changed (heartbeat race guard)
    private TypeWhisperClient.ModelInfo? _unloadedModel; // what SleepModel unloaded
    private volatile bool _waking;   // a wake-up is in progress (WAKING badge)
    private volatile bool _starting; // a start request is taking a while (STARTING badge)
    private volatile bool _twRunning = true; // heartbeat: is a TypeWhisper process alive
    private long _recordingSince;    // worker-thread only: when the current recording began
    private int _syncMisses;         // worker-thread only: consecutive "TypeWhisper isn't recording"
    private System.Threading.Timer? _syncTimer; // worker-thread only: polls state while recording
    private readonly ToolStripMenuItem _twRestartItem;
    private volatile bool _pttHeld;  // push-to-talk physically held right now
    private long _lastActivityTicks = Environment.TickCount64; // last dictation activity
    private readonly TypeWhisperApp _twApp = new();
    private readonly ToolStripMenuItem _twWakeItem;
    private readonly ToolStripMenuItem _twSleepItem;
    private readonly ToolStripMenuItem _twQuitItem;
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
    private System.Threading.Timer? _recordingTimeout; // worker-thread only
    private System.Threading.Timer? _healthTimer;      // periodic TypeWhisper reachability poll
    private volatile bool _apiReachable = true;        // last heartbeat result
    private volatile bool _apiKnown;                   // false until the first heartbeat completes
    private readonly bool _twInstalled; // checked once at startup
    private int _healthBusy;                            // 0/1 guard so heartbeats don't overlap
    private readonly System.Windows.Forms.Timer _hookWatchdog; // UI thread: dead-hook detector
    private Point _lastCursor;                          // watchdog: pointer position last tick
    private long _lastRearmTicks;                       // watchdog: rate limit
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
        _twInstalled = _twApp.IsInstalled;
        Log.Write($"app started v{CurrentVersion()} (TypeWhisper {(_twApp.CanLaunch ? "launchable" : _twInstalled ? "installed, not launchable" : "not found")})");

        // Context menu
        _statusItem  = new ToolStripMenuItem("● Idle") { Enabled = false };
        _enabledItem = new ToolStripMenuItem("Enable mouse buttons", null, OnToggleEnabled)
            { Checked = _settings.ButtonsEnabled };

        _twWakeItem  = new ToolStripMenuItem("Wake TypeWhisper", null, (_, _) => Enqueue(Cmd.Wake));
        _twSleepItem = new ToolStripMenuItem("Sleep (unload model)", null, (_, _) => Enqueue(Cmd.SleepModel));
        _twQuitItem  = new ToolStripMenuItem("Quit TypeWhisper", null, (_, _) => Enqueue(Cmd.QuitTw));
        _twRestartItem = new ToolStripMenuItem("Restart TypeWhisper", null, (_, _) => Enqueue(Cmd.RestartTw));
        var twMenu = new ToolStripMenuItem("TypeWhisper");
        twMenu.DropDownItems.AddRange(new ToolStripItem[] { _twWakeItem, _twSleepItem, _twRestartItem, _twQuitItem });
        _settingsItem = new ToolStripMenuItem("Settings…", null, OnOpenSettings);
        _startAtLoginItem = new ToolStripMenuItem("Start at login", null, OnToggleStartAtLogin)
            { Checked = IsStartAtLoginEnabled() };
        _checkUpdatesItem = new ToolStripMenuItem("Check for updates", null, OnCheckForUpdates);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_enabledItem);
        _toggleItem = new ToolStripMenuItem("Toggle dictation", null,
            (_, _) => Enqueue(Cmd.Toggle));
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripMenuItem("Reset (re-arm buttons)", null, OnReset));
        menu.Items.Add(twMenu);
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
        _tray.DoubleClick += (_, _) => Enqueue(Cmd.Toggle);
        _tray.BalloonTipClicked += OnBalloonClicked; // click an "update ready" toast to install

        // Mouse hook — events fire on the message-pump thread; just enqueue
        _hook = new MouseHook
        {
            ToggleButton = _settings.ToggleButton,
            PttButton    = _settings.PttButton,
            Enabled      = _settings.ButtonsEnabled,
        };
        _hook.ToggleDown += (_, _) => Enqueue(Cmd.Toggle);
        _hook.PttDown    += (_, _) => { _pttHeld = true;  Enqueue(Cmd.PttDown); };
        _hook.PttUp      += (_, _) => { _pttHeld = false; Enqueue(Cmd.PttUp); };

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
        ApplyState(false); // reflect a persisted "buttons disabled" right away

        // Windows can silently drop a low-level mouse hook (lock/unlock, RDP reconnect, or a
        // callback that ran too long) — "buttons stopped working until I restarted it".
        // Reset (tray) is out of reach behind full-screen RDP, so we heal automatically:
        // re-arm on unlock/reconnect, and a watchdog re-arms if the pointer moves while the
        // hook hears nothing.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        try { _lastCursor = Cursor.Position; } catch { } // else the first tick sees a "move" from (0,0)
        _hookWatchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        _hookWatchdog.Tick += OnHookWatchdog;
        _hookWatchdog.Start();

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

    private void Enqueue(Cmd cmd)
    {
        // TryAdd throws once CompleteAdding has run (shutdown); a late timer tick or
        // click then has nowhere to go, which is fine.
        try { _queue.TryAdd(new Queued(cmd, Environment.TickCount64)); }
        catch (InvalidOperationException) { }
    }

    private void WorkerLoop()
    {
        // GetConsumingEnumerable throws ObjectDisposedException when CompleteAdding races with Dispose.
        try { WorkerLoopInner(); } catch (ObjectDisposedException) { }
    }

    private void WorkerLoopInner()
    {
        foreach (var (cmd, queuedAt) in _queue.GetConsumingEnumerable())
        {
            // A press queued while the worker was stuck — a failing call (TypeWhisper hung;
            // each attempt blocks up to the HTTP timeout), a wake-up, or a "wake it?"
            // prompt — would otherwise replay seconds later, starting a recording the user
            // has long given up on. Drop such starts.
            if (cmd is Cmd.Toggle or Cmd.PttDown && queuedAt < _discardBeforeTicks)
            {
                Log.Write($"dropped stale {cmd} queued while busy");
                continue;
            }
            try
            {
                switch (cmd)
                {
                    case Cmd.Toggle:
                        if (_recording) DoStop();
                        else            PressToStart(cmd);
                        break;
                    case Cmd.PttDown:
                        if (_recording) break; // already recording (e.g. toggled on)
                        PressToStart(cmd);
                        break;
                    case Cmd.Wake:
                        if (Wake())
                            _sync.Post(_ => Notify("TypeWhisper is awake."), null);
                        break;
                    case Cmd.RestartTw:
                        if (RestartTypeWhisper())
                            _sync.Post(_ => Notify("TypeWhisper restarted and ready."), null);
                        break;
                    case Cmd.LockSleep:
                        // PC locked: nobody is dictating, so free the model now rather than
                        // waiting out the idle timer.
                        if (!_recording && _sleep == SleepState.Awake) SleepModel(manual: false, why: "lock");
                        break;
                    case Cmd.SyncRecording:
                        SyncRecording();
                        break;
                    case Cmd.SleepModel:
                        SleepModel(manual: true);
                        break;
                    case Cmd.QuitTw:
                        QuitTypeWhisper(manual: true);
                        break;
                    case Cmd.IdleCheck:
                        IdleCheck();
                        break;
                    case Cmd.PttUp:
                        // May already be stopped if a context change redirected the paste.
                        // If the button is held again by now, this release is stale — e.g.
                        // let go during a wake-up, then pressed again, so the wake started
                        // recording for the new hold. The new hold's own release stops it.
                        if (_recording && !_pttHeld) DoStop();
                        break;
                    case Cmd.ContextLost:
                        // Focus left the target window; OnContextChanged has already put
                        // focus on the original window (or the sink), so this stop pastes
                        // the transcription there rather than into a random app.
                        if (_recording) DoStop();
                        break;
                    case Cmd.AutoStop:
                        if (_recording)
                        {
                            DoStop();
                            _sync.Post(_ => Notify(
                                $"Recording auto-stopped after {_settings.AutoStopMinutes} minutes.",
                                ToolTipIcon.Warning), null);
                        }
                        break;
                    case Cmd.Reset:
                        // Clear any recording state TypeWhisper or we might still think is on,
                        // returning to a clean idle state without a process restart.
                        try { _client.Stop(); } catch { }
                        ClearRecordingState();
                        _sync.Post(_ => ApplyState(false), null);
                        break;
                }
            }
            catch (Exception ex)
            {
                _discardBeforeTicks = Environment.TickCount64;
                Log.Write($"worker error on {cmd}: {ex}");
                _sync.Post(_ => Notify(ex.Message, ToolTipIcon.Error), null);
            }
        }
    }

    // ---- Idle sleep / wake (worker thread) --------------------------------------

    private void SetSleep(SleepState s)
    {
        _sleep = s;
        Interlocked.Exchange(ref _sleepChangedTicks, Environment.TickCount64);
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    // Pre-press health check: is TypeWhisper actually able to dictate right now? Uses an
    // instant port check plus a short status call, so a healthy press costs a few ms.
    private Readiness CheckReadiness()
    {
        if (_sleep != SleepState.Awake) return Readiness.Asleep;

        double? age = _twApp.RunningForSeconds;
        bool justStarted = age is < 30;
        if (!_client.IsListening())
            return age is null ? Readiness.NotRunning : justStarted ? Readiness.Starting : Readiness.Unresponsive;

        string? status = _client.GetEngineStatus(TimeSpan.FromSeconds(3));
        if (status is null)
            return age is null ? Readiness.NotRunning : justStarted ? Readiness.Starting : Readiness.Unresponsive;
        if (status == "no_model")
        {
            SetSleep(SleepState.ModelAsleep);
            return Readiness.Asleep;
        }
        return Readiness.Ready;
    }

    // A dictation press while not recording. Checks TypeWhisper first: healthy → record;
    // asleep (we planned it) → wake per PressWhenAsleep; in a bad state (closed, crashed,
    // hung) → say so and fix it, but never start recording off the back of a recovery —
    // the user presses again once it's confirmed working.
    private void PressToStart(Cmd cmd)
    {
        var r = CheckReadiness();
        if (r == Readiness.Ready)
        {
            // TypeWhisper may already be recording without us (its own hotkey, or a start
            // that went through late) — then a toggle press means "stop".
            if (cmd == Cmd.Toggle && _client.QueryRecording() == true) { DoStop(); return; }
            DoStart();
            return;
        }

        var action = _settings.PressWhenAsleep;
        bool planned = r is Readiness.Asleep;
        Log.Write($"{cmd} with TypeWhisper {r} (sleep {_sleep}) -> {action}");

        string problem = r switch
        {
            Readiness.Asleep       => _sleep == SleepState.Off
                                        ? "TypeWhisper is closed to save memory."
                                        : "TypeWhisper is asleep (its speech model is unloaded to save memory).",
            Readiness.NotRunning   => "TypeWhisper isn't running.",
            Readiness.Starting     => "TypeWhisper is still starting up.",
            _                      => "TypeWhisper isn't responding.",
        };
        string fix = r == Readiness.Unresponsive ? "Restart it now?" : "Wake it now?";

        if (!_twApp.CanLaunch && r is Readiness.NotRunning or Readiness.Unresponsive)
        {
            _sync.Post(_ => Notify($"{problem}\nStart it from the Start menu.", ToolTipIcon.Warning), null);
            return;
        }
        if (action == WakeAction.Ask)
        {
            bool yes = false;
            _sync.Send(_ => yes = AskYesNo($"{problem}\n\n{fix}"), null);
            _discardBeforeTicks = Environment.TickCount64; // presses made while the prompt was up
            if (!yes) return;
        }
        else if (!planned)
        {
            _sync.Post(_ => Notify(r == Readiness.Unresponsive
                ? $"{problem} Restarting it…" : $"{problem} Starting it…", ToolTipIcon.Warning), null);
        }

        bool ok = r == Readiness.Unresponsive ? RestartTypeWhisper() : Wake();
        if (!ok) return;

        // Record straight away only after a planned sleep, and for push-to-talk only if the
        // button is still held — letting go during the wake-up means "never mind".
        bool record = planned && action == WakeAction.WakeAndRecord && (cmd != Cmd.PttDown || _pttHeld);
        if (record) DoStart();
        else _sync.Post(_ => Notify("TypeWhisper is ready — press again to dictate."), null);
    }

    // UI thread. Modal yes/no prompt; DefaultDesktopOnly keeps it on top and focused.
    private static bool AskYesNo(string text) =>
        MessageBox.Show(text, "Whisper Trigger",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1,
            MessageBoxOptions.DefaultDesktopOnly) == DialogResult.Yes;

    // Closes a hung (or any) TypeWhisper and starts it fresh, then waits until it's ready.
    private bool RestartTypeWhisper()
    {
        if (_recording || !_twApp.CanLaunch)
        {
            if (!_twApp.CanLaunch)
                _sync.Post(_ => Notify("Can't restart TypeWhisper — couldn't find how to launch it.", ToolTipIcon.Error), null);
            return false;
        }
        Log.Write("restarting TypeWhisper");
        _waking = true; // show WAKING through the quit too
        _sync.Post(_ => ApplyState(_recording), null);
        bool quit = _twApp.Quit();
        if (!quit)
        {
            _waking = false;
            _sync.Post(_ => { ApplyState(_recording); Notify("Couldn't close TypeWhisper to restart it.", ToolTipIcon.Error); }, null);
            return false;
        }
        _unloadedModel = null; // a fresh launch loads the selected model itself
        return Wake();
    }

    // Brings TypeWhisper to a dictation-ready state: launches it if it isn't running,
    // waits for its API, and reloads the model if we unloaded it. Shows the WAKING badge
    // meanwhile. Measured: ~3 s from model-unloaded, ~5 s from not running.
    private bool Wake()
    {
        _waking = true;
        _sync.Post(_ => ApplyState(_recording), null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!(_client.IsListening() && _client.IsReachable()))
            {
                if (!_twApp.IsRunning && !_twApp.Launch())
                {
                    _sync.Post(_ => Notify("Couldn't start TypeWhisper — is it still installed?", ToolTipIcon.Error), null);
                    return false;
                }
                long deadline = Environment.TickCount64 + 45_000;
                while (!(_client.IsListening() && _client.IsReachable()))
                {
                    if (Environment.TickCount64 > deadline)
                    {
                        Log.Write("wake: TypeWhisper API didn't come up within 45 s");
                        _sync.Post(_ => Notify(
                            "TypeWhisper started but its API isn't answering.\nIs Settings → Advanced → API Server enabled?",
                            ToolTipIcon.Error), null);
                        return false;
                    }
                    Thread.Sleep(200);
                }
            }

            // After an unload TypeWhisper reports no active model at all, so reload the one
            // we unloaded — or, if we don't know it, the one TypeWhisper has selected.
            var model = _client.GetActiveModel();
            var toLoad = model is { Loaded: true } ? null : model ?? _unloadedModel ?? _client.GetSelectedModel();
            if (model is not { Loaded: true } && toLoad is null)
            {
                Log.Write("wake: no model to load (none active, none remembered, none selected)");
            }
            else if (toLoad is not null && !_client.LoadModel(toLoad))
            {
                _sync.Post(_ => Notify("TypeWhisper is running, but its speech model failed to load.", ToolTipIcon.Error), null);
                return false;
            }

            SetSleep(SleepState.Awake);
            _apiReachable = true;
            _apiKnown = true;
            Touch();
            Log.Write($"TypeWhisper awake after {sw.ElapsedMilliseconds} ms");
            return true;
        }
        finally
        {
            _waking = false;
            _discardBeforeTicks = Environment.TickCount64; // presses made during the wake-up
            _sync.Post(_ => ApplyState(_recording), null);
        }
    }

    // Unloads TypeWhisper's speech model (tier 1). TypeWhisper itself keeps running, so
    // its API stays up and a press can reload the model in a few seconds.
    private void SleepModel(bool manual, string why = "idle")
    {
        if (_recording || _client.QueryRecording() != false)
        {
            if (manual) _sync.Post(_ => Notify("TypeWhisper is busy or unreachable — try again in a moment.", ToolTipIcon.Warning), null);
            return;
        }
        var m = _client.GetActiveModel();
        if (m is null && _client.GetEngineStatus() == "no_model")
        {
            SetSleep(SleepState.ModelAsleep); // already unloaded
            _sync.Post(_ => ApplyState(_recording), null);
            return;
        }
        if (m is null || (m.Loaded && !_client.UnloadModel(m)))
        {
            if (manual) _sync.Post(_ => Notify("Couldn't unload TypeWhisper's model.", ToolTipIcon.Warning), null);
            return;
        }
        _unloadedModel = m; // TypeWhisper forgets it on unload; Wake() reloads this one
        SetSleep(SleepState.ModelAsleep);
        Log.Write($"TypeWhisper model {m.Engine}/{m.Model} unloaded ({(manual ? "manual" : why)})");
        _sync.Post(_ => {
            ApplyState(_recording);
            if (manual) Notify("TypeWhisper is asleep — your next press wakes it.");
        }, null);
    }

    // Quits TypeWhisper entirely (tier 2). Only when we can start it again, nothing is
    // being dictated or transcribed, and — for the automatic path — its window isn't open.
    private void QuitTypeWhisper(bool manual)
    {
        if (_recording || !_twApp.CanLaunch) return;
        if (!manual && _twApp.HasVisibleWindow) return; // the user is looking at it
        // Reachable but not definitely idle (recording, transcribing, unknown) → leave it.
        if (_client.IsReachable() && _client.QueryRecording() != false)
        {
            if (manual) _sync.Post(_ => Notify("TypeWhisper is busy — try again in a moment.", ToolTipIcon.Warning), null);
            return;
        }
        if (!_twApp.Quit())
        {
            if (manual) _sync.Post(_ => Notify("Couldn't close TypeWhisper.", ToolTipIcon.Warning), null);
            return;
        }
        SetSleep(SleepState.Off);
        _apiReachable = false;
        Log.Write($"TypeWhisper quit ({(manual ? "manual" : "idle")})");
        _sync.Post(_ => {
            ApplyState(_recording);
            if (manual) Notify("TypeWhisper closed — your next press starts it again.");
        }, null);
    }

    // Queued by the heartbeat. Applies the two idle tiers.
    private void IdleCheck()
    {
        var s = _settings;
        if (!s.AutoSleep || _recording || _waking || _sleep == SleepState.Off) return;
        long idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);
        if (s.QuitAppMinutes > 0 && idleMs >= s.QuitAppMinutes * 60_000L)
            QuitTypeWhisper(manual: false);
        else if (s.SleepModelMinutes > 0 && idleMs >= s.SleepModelMinutes * 60_000L && _sleep == SleepState.Awake)
            SleepModel(manual: false);
    }

    // Fires on a thread-pool (debounce timer) thread when focus leaves the target window
    // during recording. Bring the original window back to the foreground so the paste
    // lands there; only if it's gone (closed/crashed) fall back to the sink window. Done
    // synchronously so focus is settled before the worker stops dictation.
    private void OnContextChanged(object? sender, EventArgs e)
    {
        // Defensive: with the guard disabled we never arm it, so this shouldn't fire —
        // but a setting flipped mid-recording could race an already-queued debounce.
        if (!_recording || !_settings.GuardFocusChanges) return;
        bool restored = false;
        _sync.Send(_ => {
            restored = _guard.RestoreTarget();
            if (!restored) _sink.ShowAndFocus();
        }, null);
        Enqueue(Cmd.ContextLost);
        _sync.Post(_ => Notify(
            restored
                ? "Focus changed — returned to your original window."
                : "Original window unavailable — dictation captured in a safe window.",
            ToolTipIcon.Warning), null);
    }

    private void DoStart()
    {
        Touch();
        bool ok = false;
        Exception? error = null;
        int done = 0;
        // If TypeWhisper is slow to answer (cold worker after a long idle), show STARTING
        // after a second so the press visibly registered.
        var slow = new System.Threading.Timer(_ => {
            if (Volatile.Read(ref done) == 0) { _starting = true; _sync.Post(_ => ApplyState(_recording), null); }
        }, null, 1000, Timeout.Infinite);
        try { ok = _client.Start(); }
        catch (Exception ex) { error = ex; }
        finally
        {
            Volatile.Write(ref done, 1);
            // Wait out a callback already in flight, so it can't set STARTING after we clear it.
            using var drained = new ManualResetEvent(false);
            if (slow.Dispose(drained)) drained.WaitOne(2000);
        }
        if (_starting) { _starting = false; _sync.Post(_ => ApplyState(_recording), null); }

        if (ok) { BeginRecordingState(); return; }

        // Failed or timed out — but a slow start can still have gone through late. Ask
        // TypeWhisper before reporting failure, so the badge never lies about a live mic.
        if (_client.QueryRecording() == true)
        {
            Log.Write($"start reported {(error is null ? "failure" : "an error")} but TypeWhisper is recording — adopting it");
            BeginRecordingState();
            return;
        }
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        _sync.Post(_ => Notify("Could not start recording.", ToolTipIcon.Error), null);
    }

    private void DoStop()
    {
        Touch();
        bool? confirmed = null; // null = the call threw
        Exception? error = null;
        try { confirmed = _client.Stop(); }
        catch (Exception ex) { error = ex; }

        // If the stop failed, check whether TypeWhisper is in fact still recording; if so
        // keep showing REC so the next press stops it, instead of a silent live mic.
        if (confirmed != true && _client.QueryRecording() == true)
        {
            Log.Write("stop failed and TypeWhisper is still recording — keeping REC on");
            _sync.Post(_ => Notify("TypeWhisper didn't stop recording — press again to stop it.", ToolTipIcon.Warning), null);
            return;
        }

        ClearRecordingState();
        _sync.Post(_ => {
            ApplyState(false);
            if (confirmed == false && error is null)
                Notify("Stop command was not confirmed — TypeWhisper may be out of sync.", ToolTipIcon.Warning);
        }, null);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }

    // Local bookkeeping for "a recording is live": REC badge, focus guard, auto-stop timer,
    // and a 2-second poll that keeps the badge honest if TypeWhisper stops on its own.
    private void BeginRecordingState()
    {
        _recording = true;
        _recordingSince = Environment.TickCount64;
        _syncMisses = 0;
        // Leaving the guard disarmed is what disables the feature: no target is
        // captured, so no focus change can cut the recording short.
        if (_settings.GuardFocusChanges) _guard.CaptureTarget();
        _recordingTimeout?.Dispose();
        _recordingTimeout = new System.Threading.Timer(_ => Enqueue(Cmd.AutoStop), null,
            TimeSpan.FromMinutes(_settings.AutoStopMinutes), Timeout.InfiniteTimeSpan);
        _syncTimer?.Dispose();
        _syncTimer = new System.Threading.Timer(_ => Enqueue(Cmd.SyncRecording), null, 2000, 2000);
        _sync.Post(_ => ApplyState(true), null);
    }

    private void ClearRecordingState()
    {
        _guard.Disarm();
        _recordingTimeout?.Dispose();
        _recordingTimeout = null;
        _syncTimer?.Dispose();
        _syncTimer = null;
        _recording = false;
    }

    // While recording: if TypeWhisper says it isn't (it stopped on its own, crashed, or was
    // stopped by its own hotkey), drop our REC state. Two misses in a row, and not in the
    // first few seconds, so a momentary status blip can't end a real recording early.
    private void SyncRecording()
    {
        if (!_recording || Environment.TickCount64 - _recordingSince < 3000) return;
        bool? rec = _client.QueryRecording();
        if (rec != false && _client.IsListening()) { _syncMisses = 0; return; }
        if (++_syncMisses < 2) return;

        Log.Write($"TypeWhisper {(rec == false ? "stopped recording on its own" : "went away mid-recording")} — clearing REC");
        ClearRecordingState();
        _sync.Post(_ => {
            ApplyState(false);
            Notify(rec == false ? "TypeWhisper stopped recording." : "Lost TypeWhisper mid-recording.", ToolTipIcon.Warning);
        }, null);
    }

    // Runs on a timer (thread-pool) thread. Pings the API and, on a state change,
    // updates the tray and posts a single notification — never on every beat.
    private void HealthCheck(object? _)
    {
        if (Interlocked.Exchange(ref _healthBusy, 1) == 1) return; // a beat is still in flight
        try
        {
            if (_waking) return; // a wake-up owns TypeWhisper's state right now
            long beatStart = Environment.TickCount64;

            bool up       = _client.IsReachable();
            bool wasRunning = _twRunning;
            _twRunning    = up || _twApp.IsRunning; // tells "not responding" from "not running"
            bool wasKnown = _apiKnown;
            bool wasUp    = _apiReachable;
            _apiReachable = up;
            _apiKnown     = true;

            // Sleep bookkeeping: notice TypeWhisper being woken behind our back (started
            // by hand, model reloaded by its own hotkey), and count dictation done through
            // TypeWhisper's own hotkey as activity so we don't put it to sleep mid-use.
            // Skip if the worker changed the state after this beat began (e.g. it just quit
            // TypeWhisper and our "up" reading predates that).
            bool sleepChanged = false;
            bool fresh = beatStart > Interlocked.Read(ref _sleepChangedTicks);
            // "no_model" = model unloaded (by us, or by anyone): a press must reload it.
            string? engine = up ? _client.GetEngineStatus() : null;
            bool noModel = engine == "no_model";
            if (fresh && up && _sleep == SleepState.Off)
            {
                SetSleep(noModel ? SleepState.ModelAsleep : SleepState.Awake); sleepChanged = true; Touch();
                Log.Write("TypeWhisper started outside Whisper Trigger");
            }
            else if (fresh && up && _sleep == SleepState.ModelAsleep && engine == "ready")
            {
                SetSleep(SleepState.Awake); sleepChanged = true; Touch();
                Log.Write("TypeWhisper model reloaded outside Whisper Trigger — awake");
            }
            else if (fresh && up && _sleep == SleepState.Awake && noModel)
            {
                SetSleep(SleepState.ModelAsleep); sleepChanged = true;
                Log.Write("TypeWhisper has no model loaded — treating it as asleep");
            }
            if (up && _client.QueryRecording() == true) Touch();

            if (_sleep == SleepState.Off)
            {
                // We closed it on purpose: being unreachable is expected, not a warning.
            }
            else if (!wasKnown)
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

            if (!wasKnown || wasUp != up || sleepChanged || wasRunning != _twRunning)
                _sync.Post(_ => ApplyState(_recording), null); // refresh icon/status

            if (_settings.AutoSleep && !_recording) Enqueue(Cmd.IdleCheck);
        }
        catch { /* heartbeat is best-effort */ }
        finally { Interlocked.Exchange(ref _healthBusy, 0); }
    }

    // Logs session changes (lock/unlock, RDP connect/disconnect) so the log shows what
    // preceded a "buttons stopped working" event. On lock, with idle sleep on, unloads
    // TypeWhisper's model straight away — nobody dictates into a locked PC.
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        Log.Write($"session switch: {e.Reason}");
        if (e.Reason == SessionSwitchReason.SessionLock && _settings.AutoSleep && _settings.SleepOnLock)
            Enqueue(Cmd.LockSleep);

        // Back at the desktop after a lock or (RDP) reconnect — the moments Windows is known
        // to drop low-level hooks. Re-arming a healthy hook is harmless, so just do it.
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect
                     or SessionSwitchReason.RemoteConnect)
            _sync.Post(_ => RearmHook($"after {e.Reason}"), null);
    }

    // UI thread, every 5 s. A live hook sees every mouse move, so "the pointer moved but the
    // hook heard nothing for 5 s" means it was dropped. Watching the pointer (not general
    // input) keeps typing from looking like a dead hook.
    private void OnHookWatchdog(object? sender, EventArgs e)
    {
        Point pos;
        try { pos = Cursor.Position; } catch { return; } // e.g. secure desktop while locked
        bool moved = pos != _lastCursor;
        _lastCursor = pos;
        if (!moved || !_hook.Enabled || _hook.IdleMillis < 5000) return;
        // Rate limit: if re-arming doesn't help, don't churn (or flood the log).
        if (Environment.TickCount64 - _lastRearmTicks < 60_000) return;
        RearmHook($"watchdog: pointer moved but hook silent {_hook.IdleMillis} ms");
    }

    // UI thread (the hook's thread must pump messages). Logs and re-installs the hook.
    private void RearmHook(string reason)
    {
        _lastRearmTicks = Environment.TickCount64;
        bool ok = _hook.Reinstall();
        Log.Write($"hook re-armed ({reason}) -> {(ok ? "ok" : "FAILED")}");
        if (!ok) Notify("Mouse buttons couldn't be re-armed — use Reset or restart Whisper Trigger.", ToolTipIcon.Error);
    }

    // Manual recovery (right-click → Reset): re-arm the mouse hook and clear any stuck
    // recording state, so a dropped hook can be fixed without closing and reopening the app.
    private void OnReset(object? sender, EventArgs e)
    {
        // Log how long the hook had been silent first — a large value here is strong
        // evidence the hook really had been dropped (vs. a problem elsewhere).
        Log.Write($"manual reset (hook idle was {_hook.IdleMillis}ms)");
        bool ok = _hook.Reinstall();
        Log.Write($"reset: hook reinstall -> {(ok ? "ok" : "FAILED")}");
        Enqueue(Cmd.Reset); // worker clears recording/clipboard/timer state
        Notify(ok ? "Reset — mouse buttons re-armed." : "Reset could not re-arm the hook; please restart.",
            ok ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    // ---- UI updates (main thread) ----------------------------------------------

    private void ApplyState(bool recording)
    {
        bool enabled = _hook.Enabled;
        bool waking  = _waking || _starting;
        var  sleep   = _sleep;
        bool asleep  = sleep != SleepState.Awake;
        // Surface "TypeWhisper is down" only when idle — recording/disabled/sleep take priority.
        bool apiDown = enabled && !recording && !waking && !asleep && _apiKnown && !_apiReachable;

        var gray  = Color.FromArgb(140, 140, 140);
        var red   = Color.FromArgb(210, 40, 40);
        var amber = Color.FromArgb(230, 160, 40);
        var slate = Color.FromArgb(90, 120, 170); // asleep

        var old = _icon;
        _icon = enabled
            ? MakeIcon(recording ? red : waking || apiDown ? amber : asleep ? slate : gray, filled: true)
            : MakeIcon(gray, filled: false);
        _tray.Icon = _icon;
        old.Dispose();
        string offline = !_twInstalled ? "TypeWhisper not installed"
                       : _twRunning    ? "TypeWhisper not responding"
                       :                 "TypeWhisper not running";
        string sleepText = sleep == SleepState.Off ? "TypeWhisper off (sleeping)" : "TypeWhisper asleep";
        string busy  = _starting ? "Starting dictation..." : "Waking TypeWhisper...";
        string state = !enabled ? "Disabled" : recording ? "Recording..." : waking ? busy
                     : asleep ? sleepText : apiDown ? offline : "Ready";
        _tray.Text          = $"Whisper Trigger — {state}"; // NotifyIcon.Text max 127 chars
        _statusItem.Text    = !enabled ? "○ Disabled" : recording ? "● Recording..." : waking ? $"◌ {busy}"
                            : asleep ? $"☾ {sleepText} — press to wake" : apiDown ? $"▲ {offline}" : "● Ready";
        _toggleItem.Enabled = enabled;

        _twWakeItem.Enabled    = !waking && (asleep || apiDown) && (_twApp.CanLaunch || sleep == SleepState.ModelAsleep);
        _twSleepItem.Enabled   = !waking && !recording && sleep == SleepState.Awake && _apiReachable;
        _twRestartItem.Enabled = !waking && !recording && _twApp.CanLaunch;
        _twQuitItem.Enabled    = !waking && !recording && sleep != SleepState.Off && _twApp.CanLaunch;

        if (recording)      _overlay.ShowRecording();
        else if (_starting) _overlay.ShowBusy("STARTING");
        else if (waking)    _overlay.ShowBusy("WAKING");
        else                _overlay.HideRecording();
    }

    // Whether the most recently shown balloon is the "update ready — click to install" one.
    // BalloonTipClicked doesn't say which balloon was clicked, so without this, clicking
    // any later toast (e.g. a focus warning) would silently install and restart.
    private bool _updateBalloonShowing;

    private void Notify(string msg, ToolTipIcon icon = ToolTipIcon.Info, bool isUpdateOffer = false)
    {
        _updateBalloonShowing = isUpdateOffer;
        _tray.ShowBalloonTip(4000, "Whisper Trigger", msg, icon);
    }

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
        _settings.ButtonsEnabled = _hook.Enabled;
        _settings.Save();
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
        _settings.GuardFocusChanges = r.GuardFocusChanges;
        _settings.AutoSleep         = r.AutoSleep;
        _settings.SleepModelMinutes = r.SleepModelMinutes;
        _settings.QuitAppMinutes    = r.QuitAppMinutes;
        _settings.PressWhenAsleep   = r.PressWhenAsleep;
        _settings.SleepOnLock       = r.SleepOnLock;
        _settings.Save();
        Touch(); // changing the idle timers shouldn't fire them on the very next beat

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
                OfferUpdate(info);
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
            if (token.IsCancellationRequested) return;
            // Never restart out from under a live dictation — offer it as a click instead.
            if (_recording)
            {
                OfferUpdate(info);
                return;
            }
            _sync.Post(_ => Notify(
                $"Installing update {info.TargetFullRelease.Version} — restarting…", ToolTipIcon.Info), null);
            await Task.Delay(1500, token);
            if (_recording) // started dictating during the countdown
            {
                OfferUpdate(info);
                return;
            }
            _updater.ApplyAndRestart(info); // relaunches the app
        }
        catch (OperationCanceledException) { }
        catch { /* auto-update is best-effort; stay on current version */ }
    }

    // Holds a downloaded update and invites a click on the toast to install it.
    private void OfferUpdate(UpdateInfo info)
    {
        _pendingUpdate = info;
        _sync.Post(_ => Notify(
            $"Update {info.TargetFullRelease.Version} ready — click here to install.",
            ToolTipIcon.Info, isUpdateOffer: true), null);
    }

    // Removes the "Start at login" Run entry. Called by the Velopack uninstall hook so an
    // uninstall doesn't leave Windows trying to launch a deleted exe at every login.
    public static void RemoveStartAtLogin()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch { }
    }

    // Clicking the "update ready" toast installs the downloaded update and restarts.
    private void OnBalloonClicked(object? sender, EventArgs e)
    {
        var pending = _pendingUpdate;
        if (pending is null || !_updateBalloonShowing) return;
        _updateBalloonShowing = false;
        _pendingUpdate = null;
        try { _updater.ApplyAndRestart(pending); }
        catch (Exception ex) { Notify($"Could not install update: {ex.Message}", ToolTipIcon.Error); }
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
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _queue.CompleteAdding();
            _healthTimer?.Dispose();
            _recordingTimeout?.Dispose();
            _syncTimer?.Dispose();
            _hookWatchdog.Dispose();
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
