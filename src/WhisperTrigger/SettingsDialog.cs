using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperTrigger;

// Dark settings dialog: remap which side button does toggle vs push-to-talk, set the
// auto-stop timeout, and toggle automatic updates. Edits a copy of Settings and exposes
// it via Result; the caller persists and applies on DialogResult.OK.
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
    private readonly CheckBox _autoUpdate;

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
            ToggleButton   = current.ToggleButton,
            PttButton      = current.PttButton,
            AutoStopMinutes = current.AutoStopMinutes,
            AutoUpdate     = current.AutoUpdate,
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
        ClientSize      = new Size(380, 250);
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

        _autoUpdate.Location = new Point(20, y + 6);
        Controls.Add(_autoUpdate);

        var save = new Button
        {
            Text         = "Save",
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Accent,
            ForeColor    = Color.White,
            AutoSize     = true,
            Padding      = new Padding(14, 4, 14, 4),
            Location     = new Point(278, 196),
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
            Location     = new Point(196, 196),
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
