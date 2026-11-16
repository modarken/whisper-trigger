using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperTrigger;

// What the panel shows; built by TrayApp.ApplyState on every state change.
sealed record PanelState(
    string Status,       // headline: "Ready to dictate", "Recording…", …
    Color  Dot,          // status dot colour
    bool   Recording,
    bool   Busy,         // waking / starting — the dictation button is disabled meanwhile
    string TwStatus,     // TypeWhisper card: "Ready", "Asleep — model unloaded", …
    bool   CanWake, bool CanSleep, bool CanRestart, bool CanQuit);

enum PanelCommand
{
    ToggleDictation, Wake, Sleep, Restart, QuitTw,
    ResetButtons, CheckUpdates, OpenLogs, About, Exit,
}

// What the panel needs from the tray app. Settings edits are made on Settings directly,
// then SettingsChanged() persists and applies them.
interface IPanelHost
{
    Settings Settings { get; }
    string Version { get; }
    bool StartAtLogin { get; set; }
    bool ButtonsEnabled { get; set; }
    void SettingsChanged();
    void Command(PanelCommand cmd);
}

// The tray flyout: left-click the tray icon to open it. Status, a dictation button,
// TypeWhisper controls and every setting on one dark panel; changes apply immediately.
// Closes when it loses focus, on Esc, or on another click of the tray icon.
sealed class ControlPanel : Form
{
    private static readonly Color Bg      = Color.FromArgb(32, 32, 32);
    private static readonly Color CardBg  = Color.FromArgb(43, 43, 43);
    private static readonly Color Border  = Color.FromArgb(70, 70, 70);
    private static readonly Color Fg      = Color.FromArgb(240, 240, 240);
    private static readonly Color SubFg   = Color.FromArgb(160, 160, 160);
    private static readonly Color BoxBg   = Color.FromArgb(28, 28, 28);
    private static readonly Color Accent  = Color.FromArgb(210, 40, 40);
    private static readonly Color BtnBg   = Color.FromArgb(60, 60, 60);

    private const int W = 640, H = 532, ColW = 296, LeftX = 16, RightX = 328;

    private readonly IPanelHost _host;
    private readonly float _scale;
    private readonly List<Rectangle> _cards = new();
    private bool _syncing; // true while we set controls from state — don't treat as edits

    private Color _dot = Color.Gray;
    private readonly Label _status, _twStatus, _hint, _version;
    private readonly Button _dictate;
    private readonly IconButton _wake, _sleep, _restart, _quitTw;
    private readonly ToggleSwitch _buttonsOn, _startAtLogin, _autoUpdate, _guardFocus, _autoSleep, _sleepOnLock;
    private readonly ComboBox _toggleBtn, _pttBtn, _pressWhenAsleep;
    private readonly NumericUpDown _autoStop, _sleepModel, _quitApp;

    // When the panel last hid itself on losing focus. A click on the tray icon steals focus
    // (hiding us) just before the click arrives, so a click right after means "close".
    public long HiddenAtTicks { get; private set; }

    private sealed record Choice<T>(string Label, T Value) { public override string ToString() => Label; }

    private static readonly Choice<MouseButtonId>[] ButtonChoices =
    [
        new("Forward (XButton2)", MouseButtonId.XButton2),
        new("Back (XButton1)",    MouseButtonId.XButton1),
        new("None",               MouseButtonId.None),
    ];

    private static readonly Choice<WakeAction>[] WakeChoices =
    [
        new("Wake and record", WakeAction.WakeAndRecord),
        new("Wake only",       WakeAction.Wake),
        new("Ask me first",    WakeAction.Ask),
    ];

    public ControlPanel(IPanelHost host)
    {
        _host  = host;
        _scale = DeviceDpi / 96f;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition   = FormStartPosition.Manual;
        ShowInTaskbar   = false;
        TopMost         = true;
        KeyPreview      = true;
        BackColor       = Bg;
        ForeColor       = Fg;
        AutoScaleMode   = AutoScaleMode.None; // we scale by hand (S) so sizes are known up front
        Font            = new Font("Segoe UI", 9f);
        ClientSize      = new Size(S(W), S(H));
        DoubleBuffered  = true;

        // ---- Header ----
        Add(new Label { Text = "Whisper Trigger", Font = new Font("Segoe UI Semibold", 14f), AutoSize = true },
            LeftX, 10);
        _version = Add(new Label { ForeColor = SubFg, AutoSize = true }, 170, 19);
        var close = Add(MakeButton("", BtnBg), W - 44, 12, 28, 28); // ChromeClose glyph
        close.Font = new Font(IconFont(), 8f);
        close.BackColor = Bg;
        close.FlatAppearance.MouseOverBackColor = BtnBg;
        close.Click += (_, _) => HidePanel();
        _status = Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 10f) }, 36, 46);

        // ---- Left column ----
        int y = 80;
        Card(LeftX, y, 84);
        _dictate = Add(MakeButton("Start dictation", Accent), LeftX + 12, y + 12, ColW - 24, 40);
        _dictate.Font = new Font("Segoe UI Semibold", 10.5f);
        _dictate.Click += (_, _) => _host.Command(PanelCommand.ToggleDictation);
        _hint = Add(new Label { ForeColor = SubFg, BackColor = CardBg, AutoSize = true }, LeftX + 12, y + 60);

        y = 176;
        Card(LeftX, y, 112);
        Header("TYPEWHISPER", LeftX, y);
        _twStatus = Add(new Label { BackColor = CardBg, AutoSize = true }, LeftX + 12, y + 30);
        int bw = (ColW - 24 - 18) / 4;
        // Media-style controls: icon + word, coloured so they read at a glance.
        _wake    = Add(MakeIconButton("Wake",    IconKind.Play,    Color.FromArgb(80, 200, 120)),  LeftX + 12,                y + 56, bw, 46);
        _sleep   = Add(MakeIconButton("Sleep",   IconKind.Pause,   Color.FromArgb(120, 160, 230)), LeftX + 12 + (bw + 6),     y + 56, bw, 46);
        _restart = Add(MakeIconButton("Restart", IconKind.Restart, Fg),                            LeftX + 12 + (bw + 6) * 2, y + 56, bw, 46);
        _quitTw  = Add(MakeIconButton("Stop",    IconKind.Stop,    Color.FromArgb(235, 80, 80)),   LeftX + 12 + (bw + 6) * 3, y + 56, bw, 46);
        var tips = new ToolTip { InitialDelay = 400 };
        tips.SetToolTip(_wake,    "Load the speech model (starts TypeWhisper if it isn't running)");
        tips.SetToolTip(_sleep,   "Unload the speech model to free ~1 GB — TypeWhisper keeps running");
        tips.SetToolTip(_restart, "Close TypeWhisper and start it fresh");
        tips.SetToolTip(_quitTw,  "Stop TypeWhisper completely — your next press starts it again");
        _wake.Click    += (_, _) => _host.Command(PanelCommand.Wake);
        _sleep.Click   += (_, _) => _host.Command(PanelCommand.Sleep);
        _restart.Click += (_, _) => _host.Command(PanelCommand.Restart);
        _quitTw.Click  += (_, _) => _host.Command(PanelCommand.QuitTw);

        y = 300;
        Card(LeftX, y, 176);
        Header("GENERAL", LeftX, y);
        _buttonsOn    = SwitchRow("Mouse buttons trigger dictation", LeftX, y + 32);
        _startAtLogin = SwitchRow("Start at login", LeftX, y + 66);
        _autoUpdate   = SwitchRow("Install updates automatically", LeftX, y + 100);
        _guardFocus   = SwitchRow("Stop if focus leaves the window", LeftX, y + 134);

        // ---- Right column ----
        y = 80;
        Card(RightX, y, 142);
        Header("BUTTONS", RightX, y);
        _toggleBtn = ComboRow("Toggle", ButtonChoices, RightX, y + 30);
        _pttBtn    = ComboRow("Push-to-talk", ButtonChoices, RightX, y + 64);
        _autoStop  = NumberRow("Auto-stop after (min)", 1, 60, RightX, y + 98);

        y = 234;
        Card(RightX, y, 242);
        Header("IDLE SLEEP", RightX, y);
        _autoSleep       = SwitchRow("Sleep TypeWhisper when idle", RightX, y + 32);
        _sleepOnLock     = SwitchRow("Unload as soon as the PC locks", RightX, y + 66);
        _sleepModel      = NumberRow("Unload model after (min)", 0, 1440, RightX, y + 98);
        _quitApp         = NumberRow("Stop TypeWhisper after (min)", 0, 1440, RightX, y + 130);
        _pressWhenAsleep = ComboRow("A press while asleep", WakeChoices, RightX, y + 162);
        Add(new Label
        {
            Text = "0 = never. Unloading frees ~1 GB (wakes in ~3 s).",
            ForeColor = SubFg, BackColor = CardBg, AutoSize = true,
        }, RightX + 12, y + 208);

        // ---- Footer ----
        int fx = LeftX;
        foreach (var (text, cmd) in new[]
                 {
                     ("Check for updates", PanelCommand.CheckUpdates),
                     ("Reset buttons",     PanelCommand.ResetButtons),
                     ("Open logs",         PanelCommand.OpenLogs),
                     ("About",             PanelCommand.About),
                 })
        {
            var link = MakeLink(text);
            link.Click += (_, _) => _host.Command(cmd);
            Add(link, fx, 492);
            fx += (int)(TextRenderer.MeasureText(text, link.Font).Width / _scale) + 18;
        }
        var exit = MakeLink("Exit");
        exit.Click += (_, _) => _host.Command(PanelCommand.Exit);
        Add(exit, W - 16 - (int)(TextRenderer.MeasureText("Exit", exit.Font).Width / _scale), 492);

        // ---- Settings wiring: every change applies at once ----
        _buttonsOn.CheckedChanged    += (_, _) => { if (!_syncing) _host.ButtonsEnabled = _buttonsOn.Checked; };
        _startAtLogin.CheckedChanged += (_, _) => { if (!_syncing) _host.StartAtLogin = _startAtLogin.Checked; };
        _autoUpdate.CheckedChanged   += (_, _) => Edit(s => s.AutoUpdate = _autoUpdate.Checked);
        _guardFocus.CheckedChanged   += (_, _) => Edit(s => s.GuardFocusChanges = _guardFocus.Checked);
        _autoSleep.CheckedChanged    += (_, _) => { Edit(s => s.AutoSleep = _autoSleep.Checked); SyncEnabled(); };
        _sleepOnLock.CheckedChanged  += (_, _) => Edit(s => s.SleepOnLock = _sleepOnLock.Checked);
        _autoStop.ValueChanged   += (_, _) => Edit(s => s.AutoStopMinutes = (int)_autoStop.Value);
        _sleepModel.ValueChanged += (_, _) => Edit(s => s.SleepModelMinutes = (int)_sleepModel.Value);
        _quitApp.ValueChanged    += (_, _) => Edit(s => s.QuitAppMinutes = (int)_quitApp.Value);
        _pressWhenAsleep.SelectedIndexChanged += (_, _) =>
            Edit(s => s.PressWhenAsleep = ((Choice<WakeAction>)_pressWhenAsleep.SelectedItem!).Value);
        _toggleBtn.SelectedIndexChanged += (_, _) => OnButtonChoice(_toggleBtn, _pttBtn);
        _pttBtn.SelectedIndexChanged    += (_, _) => OnButtonChoice(_pttBtn, _toggleBtn);
    }

    // ---- Public API (UI thread) ---------------------------------------------------

    // Shows the panel above the tray: near the pointer when the tray icon was clicked,
    // otherwise (opened by launching the app again) at the bottom-right of the main screen.
    public void ShowPanel(PanelState state, bool nearCursor)
    {
        LoadSettings();
        UpdateState(state);
        var cursor = Cursor.Position;
        var wa = nearCursor ? Screen.FromPoint(cursor).WorkingArea : Screen.PrimaryScreen!.WorkingArea;
        int m = S(12);
        int x = nearCursor ? Math.Clamp(cursor.X - Width / 2, wa.Left + m, wa.Right - Width - m)
                           : wa.Right - Width - m;
        // Taskbar at the bottom (the usual case) or top; left/right taskbars fall back to bottom.
        int y = nearCursor && cursor.Y < wa.Top ? wa.Top + m : wa.Bottom - Height - m;
        Location = new Point(x, y);
        ActiveControl = null; // don't reopen with a caret in whatever box was edited last
        Show();
        Activate();
        SetForegroundWindow(Handle);
    }

    public void HidePanel() => Hide();

    public void UpdateState(PanelState s)
    {
        _status.Text = s.Status;
        if (_dot != s.Dot) { _dot = s.Dot; Invalidate(new Rectangle(S(LeftX), S(46), S(18), S(22))); }
        _dictate.Text      = s.Recording ? "Stop dictation" : s.Busy ? "Waking…" : "Start dictation";
        _dictate.BackColor = s.Recording ? Color.FromArgb(150, 30, 30) : Accent;
        _dictate.Enabled   = !s.Busy || s.Recording;
        _twStatus.Text     = s.TwStatus;
        _wake.Enabled      = s.CanWake;
        _sleep.Enabled     = s.CanSleep;
        _restart.Enabled   = s.CanRestart;
        _quitTw.Enabled    = s.CanQuit;
        _syncing = true;
        _buttonsOn.Checked = _host.ButtonsEnabled;
        _syncing = false;
    }

    // ---- Settings ---------------------------------------------------------------

    private void LoadSettings()
    {
        var s = _host.Settings;
        _syncing = true;
        try
        {
            _version.Text = $"v{_host.Version}";
            _buttonsOn.Checked    = _host.ButtonsEnabled;
            _startAtLogin.Checked = _host.StartAtLogin;
            _autoUpdate.Checked   = s.AutoUpdate;
            _guardFocus.Checked   = s.GuardFocusChanges;
            _autoSleep.Checked    = s.AutoSleep;
            _sleepOnLock.Checked  = s.SleepOnLock;
            _autoStop.Value   = Math.Clamp(s.AutoStopMinutes, 1, 60);
            _sleepModel.Value = Math.Clamp(s.SleepModelMinutes, 0, 1440);
            _quitApp.Value    = Math.Clamp(s.QuitAppMinutes, 0, 1440);
            Select(_toggleBtn, ButtonChoices, s.ToggleButton);
            Select(_pttBtn, ButtonChoices, s.PttButton);
            Select(_pressWhenAsleep, WakeChoices, s.PressWhenAsleep);
            SyncEnabled();
            UpdateHint();
        }
        finally { _syncing = false; }
    }

    private void Edit(Action<Settings> change)
    {
        if (_syncing) return;
        change(_host.Settings);
        _host.SettingsChanged();
    }

    // Toggle and push-to-talk can't share a button: picking one for either frees the other.
    private void OnButtonChoice(ComboBox changed, ComboBox other)
    {
        if (_syncing) return;
        var picked = ((Choice<MouseButtonId>)changed.SelectedItem!).Value;
        if (picked != MouseButtonId.None && ((Choice<MouseButtonId>)other.SelectedItem!).Value == picked)
        {
            _syncing = true;
            Select(other, ButtonChoices, MouseButtonId.None);
            _syncing = false;
        }
        Edit(s =>
        {
            s.ToggleButton = ((Choice<MouseButtonId>)_toggleBtn.SelectedItem!).Value;
            s.PttButton    = ((Choice<MouseButtonId>)_pttBtn.SelectedItem!).Value;
        });
        UpdateHint();
    }

    private void UpdateHint()
    {
        static string Name(MouseButtonId b) => b switch
        {
            MouseButtonId.XButton2 => "Forward",
            MouseButtonId.XButton1 => "Back",
            _ => "",
        };
        var s = _host.Settings;
        var parts = new List<string>();
        if (s.ToggleButton != MouseButtonId.None) parts.Add($"{Name(s.ToggleButton)} button toggles");
        if (s.PttButton != MouseButtonId.None)    parts.Add($"hold {Name(s.PttButton)} to talk");
        _hint.Text = parts.Count == 0 ? "No mouse buttons assigned" : string.Join(" · ", parts);
    }

    private void SyncEnabled() =>
        _sleepOnLock.Enabled = _sleepModel.Enabled = _quitApp.Enabled = _autoSleep.Checked;

    private static void Select<T>(ComboBox cb, Choice<T>[] choices, T value)
    {
        int i = Array.FindIndex(choices, c => EqualityComparer<T>.Default.Equals(c.Value, value));
        cb.SelectedIndex = i >= 0 ? i : choices.Length - 1;
    }

    // ---- Layout helpers (logical 96-dpi units, scaled by S) ----------------------

    private int S(int v) => (int)Math.Round(v * _scale);

    private T Add<T>(T c, int x, int y, int w = 0, int h = 0) where T : Control
    {
        c.Location = new Point(S(x), S(y));
        if (w > 0) c.Size = new Size(S(w), S(h));
        Controls.Add(c);
        return c;
    }

    private void Card(int x, int y, int h) => _cards.Add(new Rectangle(S(x), S(y), S(ColW), S(h)));

    private void Header(string text, int x, int y) =>
        Add(new Label
        {
            Text = text, ForeColor = SubFg, BackColor = CardBg, AutoSize = true,
            Font = new Font("Segoe UI Semibold", 8f),
        }, x + 12, y + 10);

    private ToggleSwitch SwitchRow(string text, int x, int y)
    {
        var label = Add(new Label { Text = text, BackColor = CardBg, AutoSize = true }, x + 12, y + 2);
        var sw = Add(new ToggleSwitch(), x + ColW - 12 - 40, y, 40, 20);
        sw.EnabledChanged += (_, _) => label.ForeColor = sw.Enabled ? Fg : SubFg;
        label.Click += (_, _) => { if (sw.Enabled) sw.Checked = !sw.Checked; };
        return sw;
    }

    private ComboBox ComboRow<T>(string text, Choice<T>[] choices, int x, int y)
    {
        Add(new Label { Text = text, BackColor = CardBg, AutoSize = true }, x + 12, y + 4);
        var cb = Add(new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle     = FlatStyle.Flat,
            BackColor     = BoxBg,
            ForeColor     = Fg,
        }, x + ColW - 12 - 150, y);
        cb.Width = S(150);
        cb.Items.AddRange(choices);
        return cb;
    }

    private NumericUpDown NumberRow(string text, int min, int max, int x, int y)
    {
        var label = Add(new Label { Text = text, BackColor = CardBg, AutoSize = true }, x + 12, y + 3);
        var n = Add(new NumericUpDown
        {
            Minimum = min, Maximum = max,
            BackColor = BoxBg, ForeColor = Fg, BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Right,
        }, x + ColW - 12 - 64, y);
        n.Width = S(64);
        n.EnabledChanged += (_, _) => label.ForeColor = n.Enabled ? Fg : SubFg;
        return n;
    }

    private static IconButton MakeIconButton(string text, IconKind icon, Color iconColor) =>
        new() { Text = text, Icon = icon, IconColor = iconColor, BackColor = BtnBg, ForeColor = Fg, SurfaceColor = CardBg };

    private static Button MakeButton(string text, Color bg)
    {
        var b = new FlatButton
        {
            Text      = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Fg,
            Cursor    = Cursors.Hand,
            TabStop   = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.25f);
        b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(bg, 0.1f);
        b.EnabledChanged += (_, _) => b.ForeColor = b.Enabled ? Fg : Color.FromArgb(120, 120, 120);
        return b;
    }

    // Windows 11's icon font, or Windows 10's.
    private static string IconFont()
    {
        using var fonts = new System.Drawing.Text.InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    private static Label MakeLink(string text)
    {
        var l = new Label { Text = text, ForeColor = SubFg, AutoSize = true, Cursor = Cursors.Hand };
        l.MouseEnter += (_, _) => l.ForeColor = Fg;
        l.MouseLeave += (_, _) => l.ForeColor = SubFg;
        return l;
    }

    // ---- Window behaviour ---------------------------------------------------------

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            cp.ExStyle    |= 0x00000080; // WS_EX_TOOLWINDOW: out of Alt-Tab
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));  // dark scrollbars/combos
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // Windows 11 rounded corners
        }
        catch { } // cosmetic only on older Windows
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // WinForms can report a deactivate while we're still in front (e.g. the control with
        // focus got disabled). Only a real switch to another window closes the panel.
        if (GetForegroundWindow() == Handle)
        {
            BeginInvoke(() => { if (Visible) Activate(); }); // resync so the next real switch fires
            return;
        }
        HiddenAtTicks = Environment.TickCount64;
        Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var border = new Pen(Border))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        using (var card = new SolidBrush(CardBg))
            foreach (var r in _cards)
            {
                using var path = Rounded(r, S(8));
                g.FillPath(card, path);
            }
        using var dot = new SolidBrush(_dot);
        g.FillEllipse(dot, S(LeftX + 2), S(51), S(11), S(11));
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

// A button that never takes keyboard focus: clicking it leaves focus where it was, so a
// button disabling itself (Wake while waking) can't knock the panel's focus state over.
sealed class FlatButton : Button
{
    public FlatButton() => SetStyle(ControlStyles.Selectable, false);
    protected override bool ShowFocusCues => false;
}

enum IconKind { Play, Pause, Stop, Restart }

// A media-style button: a crisp, hand-drawn icon above a word. Never takes focus (see
// FlatButton). Greys out icon and text when disabled.
sealed class IconButton : Control
{
    private bool _hover, _down;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] 
    public IconKind Icon { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] 
    public Color IconColor { get; set; } = Color.White;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] 
    public Color SurfaceColor { get; set; } = Color.Black;

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)   { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { _hover = _down = false; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e)    { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(SurfaceColor); // what's behind the rounded corners

        Color bg = !Enabled ? Color.FromArgb(50, 50, 50)
                 : _down    ? ControlPaint.Dark(BackColor, 0.1f)
                 : _hover   ? ControlPaint.Light(BackColor, 0.25f)
                 :            BackColor;
        float r = Height / 8f;
        using (var path = new GraphicsPath())
        {
            var rc = new RectangleF(0, 0, Width - 1, Height - 1);
            path.AddArc(rc.X, rc.Y, r * 2, r * 2, 180, 90);
            path.AddArc(rc.Right - r * 2, rc.Y, r * 2, r * 2, 270, 90);
            path.AddArc(rc.Right - r * 2, rc.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(rc.X, rc.Bottom - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var b = new SolidBrush(bg);
            g.FillPath(b, path);
        }

        // Icon in the upper part, word below.
        float s  = Height * 0.30f;               // icon box size
        float cx = Width / 2f, cy = Height * 0.36f;
        var box  = new RectangleF(cx - s / 2, cy - s / 2, s, s);
        Color ic = Enabled ? IconColor : Color.FromArgb(105, 105, 105);
        using (var brush = new SolidBrush(ic))
        {
            switch (Icon)
            {
                case IconKind.Play:
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(box.Left + s * 0.12f, box.Top),
                        new PointF(box.Right,            cy),
                        new PointF(box.Left + s * 0.12f, box.Bottom),
                    });
                    break;
                case IconKind.Pause:
                    float bw = s * 0.32f;
                    g.FillRectangle(brush, box.Left + s * 0.06f, box.Top, bw, s);
                    g.FillRectangle(brush, box.Right - s * 0.06f - bw, box.Top, bw, s);
                    break;
                case IconKind.Stop:
                    var sq = RectangleF.Inflate(box, -s * 0.06f, -s * 0.06f);
                    g.FillRectangle(brush, sq);
                    break;
                case IconKind.Restart:
                    float pw = Math.Max(1.5f, s * 0.15f);
                    using (var pen = new Pen(ic, pw))
                        g.DrawArc(pen, RectangleF.Inflate(box, -pw / 2, -pw / 2), 30, 290);
                    // Arrowhead at the arc's end (320°), pointing along the clockwise direction.
                    double a = 320 * Math.PI / 180;
                    float rr = s / 2 - pw / 2;
                    var end = new PointF(cx + rr * (float)Math.Cos(a), cy + rr * (float)Math.Sin(a));
                    var tan = new PointF(-(float)Math.Sin(a), (float)Math.Cos(a)); // clockwise tangent
                    var nrm = new PointF((float)Math.Cos(a), (float)Math.Sin(a));
                    float h = s * 0.32f;
                    g.FillPolygon(brush, new[]
                    {
                        new PointF(end.X + tan.X * h,       end.Y + tan.Y * h),
                        new PointF(end.X + nrm.X * h * 0.8f, end.Y + nrm.Y * h * 0.8f),
                        new PointF(end.X - nrm.X * h * 0.8f, end.Y - nrm.Y * h * 0.8f),
                    });
                    break;
            }
        }

        var textRect = new Rectangle(0, (int)(Height * 0.58f), Width, (int)(Height * 0.36f));
        TextRenderer.DrawText(g, Text, Font, textRect, Enabled ? ForeColor : Color.FromArgb(120, 120, 120), bg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

// A Windows 11-style on/off switch.
sealed class ToggleSwitch : Control
{
    private static readonly Color On      = Color.FromArgb(210, 40, 40);
    private static readonly Color OffTrack = Color.FromArgb(70, 70, 70);
    private static readonly Color Knob    = Color.FromArgb(245, 245, 245);

    private bool _checked;
    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float d = r.Height;
        Color track = _checked ? On : OffTrack;
        if (!Enabled) track = Color.FromArgb(55, 55, 55);
        using (var path = new GraphicsPath())
        {
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            using var b = new SolidBrush(track);
            g.FillPath(b, path);
        }
        float k = d - 6;
        float kx = _checked ? r.Right - k - 3 : r.X + 3;
        using var kb = new SolidBrush(Enabled ? Knob : Color.FromArgb(130, 130, 130));
        g.FillEllipse(kb, kx, r.Y + 3, k, k);
    }
}
