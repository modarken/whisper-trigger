using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperTrigger;

// Dark settings dialog: remap which side button does toggle vs push-to-talk, set the
// auto-stop timeout, toggle the focus guard, and toggle automatic updates. Edits a copy
// of Settings and exposes it via Result; the caller persists and applies on DialogResult.OK.
sealed class SettingsDialog : Form
{
    private static readonly Color Bg     = Color.FromArgb(32, 32, 32);
    private static readonly Color Fg     = Color.FromArgb(235, 235, 235);
    private static readonly Color SubFg  = Color.FromArgb(170, 170, 170);
    private static readonly Color BoxBg  = Color.FromArgb(24, 24, 24);
    private static readonly Color Accent = Color.FromArgb(210, 40, 40);

    private readonly ComboBox _toggle;
    private readonly ComboBox _ptt;
    private readonly NumericUpDown _autoStop;
    private readonly CheckBox _guardFocus;
    private readonly CheckBox _autoUpdate;
    private readonly CheckBox _autoSleep;
    private readonly CheckBox _sleepOnLock;
    private readonly NumericUpDown _sleepModel;
    private readonly NumericUpDown _quitApp;
    private readonly ComboBox _pressWhenAsleep;

    private sealed record WakeChoice(string Label, WakeAction Action)
    {
        public override string ToString() => Label;
    }

    private static WakeChoice[] WakeChoices =>
    [
        new("Wake and start recording", WakeAction.WakeAndRecord),
        new("Wake only",                WakeAction.Wake),
        new("Ask me first",             WakeAction.Ask),
    ];

    public Settings Result { get; }

    private sealed record ButtonChoice(string Label, MouseButtonId Id)
    {
        public override string ToString() => Label;
    }

    private static ButtonChoice[] Choices =>
    [
        new("Forward (XButton2)", MouseButtonId.XButton2),
        new("Back (XButton1)",    MouseButtonId.XButton1),
        new("None (disabled)",    MouseButtonId.None),
    ];

    public SettingsDialog(Settings current)
    {
        Result = new Settings
        {
            ToggleButton    = current.ToggleButton,
            PttButton       = current.PttButton,
            AutoStopMinutes = current.AutoStopMinutes,
            AutoUpdate      = current.AutoUpdate,
            GuardFocusChanges = current.GuardFocusChanges,
            AutoSleep         = current.AutoSleep,
            SleepModelMinutes = current.SleepModelMinutes,
            QuitAppMinutes    = current.QuitAppMinutes,
            PressWhenAsleep   = current.PressWhenAsleep,
            SleepOnLock       = current.SleepOnLock,
            ButtonsEnabled    = current.ButtonsEnabled,
        };

        Text            = "Whisper Trigger — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        ShowInTaskbar   = false;
        MinimizeBox     = false;
        MaximizeBox     = false;
        BackColor       = Bg;
        ForeColor       = Fg;
        Font            = new Font("Segoe UI", 9.5f);
        ClientSize      = new Size(420, 530);
        Padding         = new Padding(18);

        _toggle = MakeCombo(current.ToggleButton);
        _ptt    = MakeCombo(current.PttButton);

        _autoStop = new NumericUpDown
        {
            Minimum   = 1,
            Maximum   = 60,
            Value     = Math.Clamp(current.AutoStopMinutes, 1, 60),
            Width     = 70,
            BackColor = BoxBg,
            ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle,
        };

        _guardFocus = new CheckBox
        {
            Text      = "Stop recording if focus leaves the target window",
            Checked   = current.GuardFocusChanges,
            ForeColor = Fg,
            AutoSize  = true,
        };

        var guardHint = new Label
        {
            Text        = "Keeps the transcription out of whatever app stole focus. "
                        + "Turn off if recording keeps cutting out on you.",
            ForeColor   = SubFg,
            AutoSize    = true,
            MaximumSize = new Size(360, 0),
        };

        _autoUpdate = new CheckBox
        {
            Text      = "Install updates automatically on launch",
            Checked   = current.AutoUpdate,
            ForeColor = Fg,
            AutoSize  = true,
        };

        int y = 18;
        AddRow("Toggle button", _toggle, ref y);
        AddRow("Push-to-talk button", _ptt, ref y);
        AddRow("Auto-stop after (minutes)", _autoStop, ref y);

        _guardFocus.Location = new Point(20, y + 6);
        Controls.Add(_guardFocus);

        guardHint.Location = new Point(38, y + 30);
        Controls.Add(guardHint);

        _autoUpdate.Location = new Point(20, y + 76);
        Controls.Add(_autoUpdate);

        // ---- TypeWhisper idle sleep ----
        y += 116;
        var header = new Label
        {
            Text      = "TypeWhisper idle sleep",
            Font      = new Font("Segoe UI Semibold", 10f),
            ForeColor = Fg,
            AutoSize  = true,
            Location  = new Point(20, y),
        };
        Controls.Add(header);
        y += 26;

        _autoSleep = new CheckBox
        {
            Text      = "Put TypeWhisper to sleep when I'm not dictating",
            Checked   = current.AutoSleep,
            ForeColor = Fg,
            AutoSize  = true,
            Location  = new Point(20, y),
        };
        Controls.Add(_autoSleep);
        y += 28;

        _sleepOnLock = new CheckBox
        {
            Text      = "Also unload the model as soon as the PC locks",
            Checked   = current.SleepOnLock,
            ForeColor = Fg,
            AutoSize  = true,
            Location  = new Point(38, y),
        };
        Controls.Add(_sleepOnLock);
        y += 32;

        _sleepModel = MakeMinutes(current.SleepModelMinutes);
        _quitApp    = MakeMinutes(current.QuitAppMinutes);
        AddRow("Unload model after (min)", _sleepModel, ref y);
        AddRow("Quit TypeWhisper after (min)", _quitApp, ref y);

        var sleepHint = new Label
        {
            Text        = "0 skips that step. Unloading frees ~1 GB and wakes in ~3 s; "
                        + "quitting frees ~2 GB and wakes in ~5 s.",
            ForeColor   = SubFg,
            AutoSize    = true,
            MaximumSize = new Size(380, 0),
            Location    = new Point(20, y),
        };
        Controls.Add(sleepHint);
        y += 40;

        _pressWhenAsleep = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 190,
            BackColor     = BoxBg,
            ForeColor     = Fg,
            FlatStyle     = FlatStyle.Flat,
        };
        _pressWhenAsleep.Items.AddRange(WakeChoices);
        _pressWhenAsleep.SelectedIndex = Math.Max(0, Array.FindIndex(WakeChoices, c => c.Action == current.PressWhenAsleep));
        AddRow("When asleep, a press", _pressWhenAsleep, ref y);

        void SyncEnabled() => _sleepModel.Enabled = _quitApp.Enabled = _sleepOnLock.Enabled = _autoSleep.Checked;
        _autoSleep.CheckedChanged += (_, _) => SyncEnabled();
        SyncEnabled();

        var save = new Button
        {
            Text         = "Save",
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Accent,
            ForeColor    = Color.White,
            AutoSize     = true,
            Padding      = new Padding(14, 4, 14, 4),
            Location     = new Point(318, 482),
        };
        save.FlatAppearance.BorderSize = 0;
        save.Click += OnSave;

        var cancel = new Button
        {
            Text         = "Cancel",
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Color.FromArgb(60, 60, 60),
            ForeColor    = Fg,
            AutoSize     = true,
            Padding      = new Padding(10, 4, 10, 4),
            Location     = new Point(236, 482),
        };
        cancel.FlatAppearance.BorderSize = 0;

        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(save);
        Controls.Add(cancel);
    }

    private ComboBox MakeCombo(MouseButtonId selected)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width         = 170,
            BackColor     = BoxBg,
            ForeColor     = Fg,
            FlatStyle     = FlatStyle.Flat,
        };
        cb.Items.AddRange(Choices);
        cb.SelectedIndex = Array.FindIndex(Choices, c => c.Id == selected);
        if (cb.SelectedIndex < 0) cb.SelectedIndex = Choices.Length - 1; // None
        return cb;
    }

    private static NumericUpDown MakeMinutes(int value) => new()
    {
        Minimum     = 0,
        Maximum     = 1440,
        Value       = Math.Clamp(value, 0, 1440),
        Width       = 70,
        BackColor   = BoxBg,
        ForeColor   = Fg,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private void AddRow(string label, Control field, ref int y)
    {
        var lbl = new Label
        {
            Text      = label,
            ForeColor = SubFg,
            AutoSize  = true,
            Location  = new Point(20, y + 4),
        };
        field.Location = new Point(190, y);
        Controls.Add(lbl);
        Controls.Add(field);
        y += 36;
    }

    private void OnSave(object? sender, EventArgs e)
    {
        var toggle = ((ButtonChoice)_toggle.SelectedItem!).Id;
        var ptt    = ((ButtonChoice)_ptt.SelectedItem!).Id;

        // Two actions on the same physical button is ambiguous — reject it.
        if (toggle != MouseButtonId.None && toggle == ptt)
        {
            MessageBox.Show(this,
                "Toggle and push-to-talk can't use the same button.",
                "Whisper Trigger", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None; // keep dialog open
            return;
        }

        Result.ToggleButton    = toggle;
        Result.PttButton       = ptt;
        Result.AutoStopMinutes = (int)_autoStop.Value;
        Result.AutoUpdate      = _autoUpdate.Checked;
        Result.GuardFocusChanges = _guardFocus.Checked;
        Result.AutoSleep         = _autoSleep.Checked;
        Result.SleepOnLock       = _sleepOnLock.Checked;
        Result.SleepModelMinutes = (int)_sleepModel.Value;
        Result.QuitAppMinutes    = (int)_quitApp.Value;
        Result.PressWhenAsleep   = ((WakeChoice)_pressWhenAsleep.SelectedItem!).Action;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
