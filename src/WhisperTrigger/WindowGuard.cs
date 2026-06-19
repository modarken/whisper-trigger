using System.Runtime.InteropServices;

namespace WhisperTrigger;

// Watches the system foreground window while dictation is recording. If focus moves
// away from the window that was focused when recording started — to anything other than
// our own sink window — it raises ContextChanged so the paste can be redirected into a
// window we control instead of leaking into a random app.
//
// The WinEvent callback fires on the thread that installed the hook (the WinForms
// message-pump thread). The debounce re-check runs on a timer thread, so ContextChanged
// is raised on a thread-pool thread — handlers must marshal any UI work themselves.
sealed class WindowGuard : IDisposable
{
    // Raised (on a thread-pool thread) when focus leaves the captured target during
    // recording and stays off it. Fires at most once per recording session.
    public event EventHandler? ContextChanged;

    // How long focus must stay off the target before we react, so a notification toast
    // that grabs and immediately returns focus doesn't trigger us. Set to 0 for instant.
    private const int DebounceMs = 200;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT  = 0x0000;

    private readonly WinEventDelegate _proc; // kept alive to prevent GC of the callback
    private IntPtr _hook;

    private volatile bool _recording;            // armed only between Start and Stop
    private volatile bool _fired;                 // once-per-session latch
    private IntPtr _target;                       // window focused when recording started
    private IntPtr _sinkHandle;                   // our own window — never a "change"
    private System.Threading.Timer? _debounce;    // delayed re-check

    public WindowGuard()
    {
        _proc = WinEventCallback;
        _hook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install foreground WinEvent hook (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    // The sink window's handle, so the guard never mistakes our own window for a context
    // change. Set once the sink form's handle exists.
    public IntPtr SinkHandle { get => _sinkHandle; set => _sinkHandle = value; }

    // Called on the worker thread when recording starts: remember the focused window and
    // arm the hook.
    public void CaptureTarget()
    {
        _target = GetForegroundWindow();
        _fired = false;
        _recording = true;
    }

    // Called on the worker thread when recording stops (for any reason): disarm.
    public void Disarm() => _recording = false;

    // The window that was focused when recording started.
    public IntPtr Target => _target;

    // Whether the captured target window still exists (false if it was closed/crashed).
    public bool TargetIsAlive() => _target != IntPtr.Zero && IsWindow(_target);

    // Tries to bring the captured target window back to the foreground so the paste lands
    // in it. Returns true only if the target is alive and is now actually the foreground
    // window; false means the caller should fall back to the sink. UI-thread only.
    public bool RestoreTarget()
    {
        if (!TargetIsAlive()) return false;
        ForceForeground(_target);
        return GetForegroundWindow() == _target;
    }

    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!_recording || _fired) return;
        if (hwnd == IntPtr.Zero || hwnd == _target || hwnd == _sinkHandle) return;

        // Re-check after a short delay so a momentary focus grab doesn't trigger us.
        // (One-shot; resetting it on each event coalesces a flurry of changes.)
        if (_debounce is null)
            _debounce = new System.Threading.Timer(_ => DebouncedCheck(), null,
                DebounceMs, Timeout.Infinite);
        else
            _debounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void DebouncedCheck()
    {
        if (!_recording || _fired) return;
        IntPtr now = GetForegroundWindow();
        if (now == IntPtr.Zero || now == _target || now == _sinkHandle) return;
        Trigger();
    }

    private void Trigger()
    {
        if (_fired) return;
        _fired = true;
        ContextChanged?.Invoke(this, EventArgs.Empty);
    }

    // Forces a window to the foreground, defeating Windows' focus-steal protection by
    // briefly attaching our thread's input to the current foreground thread.
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        uint thisThread = GetCurrentThreadId();
        uint fgThread   = GetWindowThreadProcessId(GetForegroundWindow(), out _);

        bool attached = fgThread != 0 && fgThread != thisThread
            && AttachThreadInput(thisThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }
    }

    public void Dispose()
    {
        _recording = false;
        _debounce?.Dispose();
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);
}
