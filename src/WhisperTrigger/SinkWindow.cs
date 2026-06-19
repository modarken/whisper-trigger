using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperTrigger;

// A small, controlled destination for dictation text. Used as a fallback when focus
// moved off the original window and that window is gone (closed/crashed) so we can't
// restore it: we show this window and force focus into its text box, so TypeWhisper
// pastes here instead of into whatever app grabbed focus. The text just sits here for the
// user to read/copy; Esc or Close dismisses it. Created once, shown/hidden.
sealed class SinkWindow : Form
{
    private readonly TextBox _text;

    // Colours for a clean dark card.
    private static readonly Color Bg     = Color.FromArgb(32, 32, 32);
    private static readonly Color Fg     = Color.FromArgb(235, 235, 235);
    private static readonly Color SubFg  = Color.FromArgb(170, 170, 170);
    private static readonly Color BoxBg  = Color.FromArgb(24, 24, 24);
    private static readonly Color Accent = Color.FromArgb(210, 40, 40);

    public SinkWindow()
    {
        Text            = "Whisper Trigger — Dictation captured";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        ShowInTaskbar   = false;
        MinimizeBox     = false;
        MaximizeBox     = false;
        TopMost         = true;
        BackColor       = Bg;
        ForeColor       = Fg;
        Font            = new Font("Segoe UI", 9.5f);
        ClientSize      = new Size(440, 250);
        Padding         = new Padding(16);
        KeyPreview      = true; // so Esc closes regardless of focused child

        var header = new Label
        {
            Text      = "⚠  Original window unavailable — dictation captured here",
            Dock      = DockStyle.Top,
            Height    = 26,
            Font      = new Font("Segoe UI Semibold", 11f),
            ForeColor = Fg,
        };

        var sub = new Label
        {
            Text      = "The window you were dictating into is gone, so the text was placed "
                      + "here instead of pasted somewhere unexpected. Copy what you need, then close.",
            Dock      = DockStyle.Top,
            Height    = 46,
            ForeColor = SubFg,
        };

        _text = new TextBox
        {
            Multiline   = true,
            Dock        = DockStyle.Fill,
            ScrollBars  = ScrollBars.Vertical,
            BackColor   = BoxBg,
            ForeColor   = Fg,
            BorderStyle = BorderStyle.FixedSingle,
            Font        = new Font("Segoe UI", 10.5f),
        };

        var buttons = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height        = 40,
            Padding       = new Padding(0, 8, 0, 0),
        };
        var close = new Button
        {
            Text      = "Close",
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Accent,
            ForeColor = Color.White,
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Hide();
        var copy = new Button
        {
            Text      = "Copy",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Fg,
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
        };
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) => { if (_text.TextLength > 0) Clipboard.SetText(_text.Text); };
        buttons.Controls.Add(close);
        buttons.Controls.Add(copy);
        CancelButton = close; // Esc triggers it

        // Add in reverse z-order so Fill sits between the docked bands.
        Controls.Add(_text);
        Controls.Add(buttons);
        Controls.Add(sub);
        Controls.Add(header);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryApplyModernChrome(Handle);
    }

    // Closing the window just hides it so it can be reused; clear the text for next time.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _text.Clear();
    }

    // Show, force to the foreground, and focus the text box so the paste lands in it.
    // Must be called on the UI thread.
    public void ShowAndFocus()
    {
        _text.Clear();
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        WindowGuard.ForceForeground(Handle);
        _text.Focus();
    }

    private static void TryApplyModernChrome(IntPtr hwnd)
    {
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch { } // older Windows without these attributes — cosmetic only
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE   = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE  = 33;
    private const int DWMWCP_ROUND                    = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
