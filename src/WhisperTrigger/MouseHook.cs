using System.Runtime.InteropServices;

namespace WhisperTrigger;

// Low-level mouse hook that intercepts side buttons BEFORE they reach full-screen RDP.
// Raises events on the Windows Forms message-pump thread; callers must not block in handlers.
sealed class MouseHook : IDisposable
{
    public event EventHandler? ToggleDown;  // mapped toggle button pressed
    public event EventHandler? PttDown;     // mapped push-to-talk button pressed
    public event EventHandler? PttUp;       // mapped push-to-talk button released

    // Which physical side button drives each action. Configurable at runtime; a button
    // not assigned to either action is passed through to other apps unchanged.
    public MouseButtonId ToggleButton { get; set; } = MouseButtonId.XButton2;
    public MouseButtonId PttButton    { get; set; } = MouseButtonId.XButton1;

    private const int WH_MOUSE_LL  = 14;
    private const int HC_ACTION     = 0;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP   = 0x020C;
    private const int XBUTTON1 = 0x0001; // back  → push-to-talk
    private const int XBUTTON2 = 0x0002; // forward → toggle

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    // When false, all button events pass through to other apps unchanged.
    public bool Enabled { get; set; } = true;

    private IntPtr _handle;
    private readonly LowLevelMouseProc _proc; // must be kept alive to prevent GC
    private long _lastCallbackTicks = Environment.TickCount64; // updated on every mouse event

    public MouseHook()
    {
        _proc = HookCallback;
        _handle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install low-level mouse hook (Win32 error {Marshal.GetLastWin32Error()}).");
        Log.Write("mouse hook installed");
    }

    // How long since the hook last received ANY mouse event. A healthy hook fires on every
    // mouse move; if this keeps climbing while the machine is in use, the hook has been
    // dropped (timeout, session/desktop switch, RDP reconnect) and should be reinstalled.
    public long IdleMillis => Environment.TickCount64 - _lastCallbackTicks;

    public bool IsInstalled => _handle != IntPtr.Zero;

    // Tears down and re-establishes the hook. Must run on the message-pump thread that
    // owns the hook (low-level hooks require their installing thread to pump messages).
    // Returns true if the hook is active afterwards.
    public bool Reinstall()
    {
        var old = _handle;
        _handle = IntPtr.Zero;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        _handle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        _lastCallbackTicks = Environment.TickCount64; // give the fresh hook a clean baseline
        if (_handle == IntPtr.Zero)
            Log.Write($"mouse hook reinstall FAILED (Win32 error {Marshal.GetLastWin32Error()})");
        return _handle != IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION)
        {
            _lastCallbackTicks = Environment.TickCount64; // proof the hook is alive
        }
        if (nCode == HC_ACTION && Enabled)
        {
            int msg = (int)wParam;
            if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int raw = (short)((data.mouseData >> 16) & 0xFFFF);
                var id = raw == XBUTTON1 ? MouseButtonId.XButton1
                       : raw == XBUTTON2 ? MouseButtonId.XButton2
                       : MouseButtonId.None;

                if (id != MouseButtonId.None && id == ToggleButton)
                {
                    if (msg == WM_XBUTTONDOWN) ToggleDown?.Invoke(this, EventArgs.Empty);
                    return (IntPtr)1; // swallow — RDP never sees it
                }
                if (id != MouseButtonId.None && id == PttButton)
                {
                    (msg == WM_XBUTTONDOWN ? PttDown : PttUp)?.Invoke(this, EventArgs.Empty);
                    return (IntPtr)1;
                }
                // Unmapped side button → fall through and pass it on to other apps.
            }
        }
        return CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
