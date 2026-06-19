using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WhisperTrigger;

// A proper About dialog (replaces the old balloon-tip "about"). Dark card matching the
// sink window, showing the app name, version, a one-line description, a repo link, and
// a Check-for-updates button wired back to the tray app's updater.
sealed class AboutBox : Form
{
    private const string RepoUrl = "https://github.com/modarken/whisper-trigger";

    private static readonly Color Bg     = Color.FromArgb(32, 32, 32);
    private static readonly Color Fg     = Color.FromArgb(235, 235, 235);
    private static readonly Color SubFg  = Color.FromArgb(170, 170, 170);
    private static readonly Color Accent = Color.FromArgb(210, 40, 40);
    private static readonly Color LinkFg = Color.FromArgb(110, 170, 255);

    public AboutBox(string version, Icon appIcon, Action onCheckForUpdates)
    {
        Text            = "About Whisper Trigger";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterScreen;
        ShowInTaskbar   = false;
        MinimizeBox     = false;
        MaximizeBox     = false;
        BackColor       = Bg;
        ForeColor       = Fg;
        Font            = new Font("Segoe UI", 9.5f);
        ClientSize      = new Size(420, 232);
        Padding         = new Padding(18);

        var icon = new PictureBox
        {
            Image    = appIcon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size     = new Size(48, 48),
            Location = new Point(18, 18),
        };

        var title = new Label
        {
            Text      = "Whisper Trigger",
            Font      = new Font("Segoe UI Semibold", 14f),
            ForeColor = Fg,
            AutoSize  = true,
            Location  = new Point(78, 20),
        };

        var ver = new Label
        {
            Text      = $"Version {version}",
            ForeColor = SubFg,
            AutoSize  = true,
            Location  = new Point(80, 50),
        };

        var desc = new Label
        {
            Text      = "Trigger TypeWhisper dictation with mouse side buttons — even when\n"
                      + "full-screen Remote Desktop captures all keyboard input.",
            ForeColor = Fg,
            AutoSize  = false,
            Size      = new Size(384, 44),
            Location  = new Point(18, 84),
        };

        var link = new LinkLabel
        {
            Text         = RepoUrl,
            LinkColor    = LinkFg,
            ActiveLinkColor = LinkFg,
            AutoSize     = true,
            Location     = new Point(18, 132),
        };
        link.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); } catch { }
        };

        var check = new Button
        {
            Text      = "Check for updates",
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(60, 60, 60),
            ForeColor = Fg,
            AutoSize  = true,
            Padding   = new Padding(10, 4, 10, 4),
            Location  = new Point(18, 176),
        };
        check.FlatAppearance.BorderSize = 0;
        check.Click += (_, _) => onCheckForUpdates();

        var ok = new Button
        {
            Text         = "OK",
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.Flat,
            BackColor    = Accent,
            ForeColor    = Color.White,
            AutoSize     = true,
            Padding      = new Padding(14, 4, 14, 4),
            Location     = new Point(320, 176),
        };
        ok.FlatAppearance.BorderSize = 0;

        AcceptButton = ok;
        CancelButton = ok;

        Controls.AddRange([icon, title, ver, desc, link, check, ok]);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));   // immersive dark mode
            int round = 2;
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));  // rounded corners
        }
        catch { } // cosmetic only on older Windows
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
