using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperTrigger;

// A small always-on-top "REC" badge shown while recording. Its whole reason to exist:
// in a full-screen RDP session the tray icon is hidden, so there is otherwise no sign
// that dictation is live. The window is click-through and never activates, so it floats
// over RDP without stealing focus (which would otherwise trip the WindowGuard).
sealed class OverlayWindow : Form
{
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020; // mouse passes through
    private const int WS_EX_NOACTIVATE  = 0x08000000; // never takes focus
    private const int WS_EX_TOOLWINDOW  = 0x00000080; // out of Alt-Tab

    private static readonly Color Pill = Color.FromArgb(28, 28, 28);
    private static readonly Color Dot    = Color.FromArgb(225, 45, 45);
    private static readonly Color TextFg = Color.FromArgb(240, 240, 240);

    private readonly System.Windows.Forms.Timer _topmostKeeper;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        Size            = new Size(96, 34);
        BackColor       = Pill;
        Opacity         = 0.9;
        DoubleBuffered  = true;
        Region          = RoundedRegion(new Rectangle(Point.Empty, Size), 16);

        // Re-assert top-most periodically: a full-screen RDP window is itself top-most,
        // and because we never activate, the z-order can otherwise drift below it.
        _topmostKeeper = new System.Windows.Forms.Timer { Interval = 1000 };
        _topmostKeeper.Tick += (_, _) => { if (Visible) ForceTopMost(); };
    }

    // WinForms would otherwise activate the form on Show(); we must not steal focus.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public void ShowRecording()
    {
        PositionTopCenter();
        if (!Visible) ShowInactive();   // show without activating
        ForceTopMost();
        _topmostKeeper.Start();
    }

    public void HideRecording()
    {
        _topmostKeeper.Stop();
        if (Visible) Hide();
    }

    private void ShowInactive()
    {
        ShowWindow(Handle, SW_SHOWNOACTIVATE);
        Visible = true;
    }

    private void PositionTopCenter()
    {
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + 12);
    }

    private void ForceTopMost() =>
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var dot = new SolidBrush(Dot);
        g.FillEllipse(dot, 16, Height / 2 - 6, 12, 12);

        using var txt = new SolidBrush(TextFg);
        using var font = new Font("Segoe UI Semibold", 10.5f);
        g.DrawString("REC", font, txt, 36, Height / 2 - 11);
    }

    private static Region RoundedRegion(Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return new Region(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _topmostKeeper.Dispose();
        base.Dispose(disposing);
    }

    private const int SW_SHOWNOACTIVATE = 4;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
