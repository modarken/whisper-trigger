using System.Runtime.InteropServices;

namespace WhisperTrigger;

// Low-level mouse hook that intercepts side buttons BEFORE they reach full-screen RDP.
// Raises events on the Windows Forms message-pump thread; callers must not block in handlers.
sealed class MouseHook : IDisposable
{
    public event EventHandler? ToggleDown;  // XButton2 (forward) pressed
    public event EventHandler? PttDown;     // XButton1 (back) pressed
    public event EventHandler? PttUp;       // XButton1 (back) released

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

    public MouseHook()
    {
        _proc = HookCallback;
        _handle = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Failed to install low-level mouse hook (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION && Enabled)
        {
            int msg = (int)wParam;
            if (msg is WM_XBUTTONDOWN or WM_XBUTTONUP)
            {
                var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int button = (short)((data.mouseData >> 16) & 0xFFFF);

                if (button == XBUTTON2)
                {
                    if (msg == WM_XBUTTONDOWN) ToggleDown?.Invoke(this, EventArgs.Empty);
                    return (IntPtr)1; // swallow — RDP never sees it
                }
                if (button == XBUTTON1)
                {
                    (msg == WM_XBUTTONDOWN ? PttDown : PttUp)?.Invoke(this, EventArgs.Empty);
                    return (IntPtr)1;
                }
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
