# Whisper Trigger

Trigger [TypeWhisper](https://www.typewhisper.com) dictation via mouse side buttons — works even when full-screen Remote Desktop captures all keyboard input.

## Why this exists

Two problems, one small tool:

1. **Full-screen Remote Desktop swallows keyboard hotkeys.** When an RDP session is
   full-screen, Windows forwards *all* keystrokes into the remote machine — so a local
   dictation hotkey never fires on your own PC. Changing it to a single key like `F8`
   doesn't help; that gets forwarded too. You're locked out of triggering dictation
   locally by keyboard.

2. **TypeWhisper can't bind a mouse button.** TypeWhisper triggers on a keyboard
   hotkey, but it has no way to map a mouse side button to start/stop dictation.

Whisper Trigger bridges both. A low-level Windows mouse hook captures a side button
**locally** and *swallows* it, so full-screen RDP never sees it — then it calls
TypeWhisper's local API directly to start/stop dictation. No simulated keystroke is
involved, so there's nothing for RDP to intercept. The result: a mouse-button trigger
TypeWhisper doesn't offer on its own, that keeps working inside full-screen RDP.

It's deliberately a small, single-purpose workaround — not a replacement for TypeWhisper.
TypeWhisper still does all the recording, transcription, and pasting; this just gives it
a trigger that survives the RDP keyboard problem.

## Install

Download the latest installer and run it:

**[⬇ Download Whisper Trigger (latest)](https://github.com/modarken/whisper-trigger/releases/latest)** → `WhisperTrigger-win-Setup.exe`

- Windows 10/11, 64-bit. Nothing else to install — the .NET runtime is bundled.
- The installer is unsigned, so Windows SmartScreen may warn "unknown publisher."
  Click **More info → Run anyway**.
- The app lives in the system tray and updates itself (see [Updates](#updates)).

## Prerequisites

1. [TypeWhisper](https://www.typewhisper.com) installed and running
2. API server enabled: TypeWhisper → **Settings → Advanced → API Server**

If TypeWhisper is missing or its API is off, the tray icon turns amber and a notification
tells you what to fix.

## Usage

### Mouse buttons

| Button | Action |
|--------|--------|
| Back (XButton1) | **Push-to-talk** — hold to record, release to stop |
| Forward (XButton2) | **Toggle** — press to start, press again to stop |

Buttons are remappable — see [Control panel](#control-panel). The panel's **Start dictation**
button also toggles dictation.

### Recording indicator

A small **REC badge** appears at the top-center of the screen while recording, so you can
tell dictation is live even when the tray icon is hidden behind a full-screen Remote
Desktop window. While TypeWhisper is waking up after a press, the badge shows **WAKING**
instead.

### Tray icon states

| Icon | Meaning |
|------|---------|
| ● Gray | Idle — listening for button presses |
| ● Red | Recording |
| ● Amber | Waking/starting, or TypeWhisper is not running / not responding / not installed |
| ● Slate blue | TypeWhisper is asleep (model unloaded, or closed) — the next press wakes it |
| ○ Outlined | Mouse buttons disabled (pass-through to other apps) |

### TypeWhisper idle sleep

TypeWhisper holds its speech model in memory all the time — roughly 2 GB while idle.
Whisper Trigger can put it to sleep when you aren't dictating and wake it on your next
button press:

1. **Unload the model** after a short idle (default 15 min). Frees ~1 GB; TypeWhisper keeps
   running and reloads the model in ~3 s.
2. **Stop TypeWhisper** after a long idle (default 60 min). Frees everything; Whisper
   Trigger relaunches it minimized in ~5 s.

With idle sleep on, locking the PC also unloads the model right away (can be turned off).

What a press does while it's asleep is configurable: **wake and start recording** (push-to-talk
only records if you're still holding the button once it's ready), **wake only**, or **ask
first**.

TypeWhisper is never closed while it's recording or transcribing, or while its own window is
open. The control panel's **TypeWhisper** card has media-style buttons: **▶ Wake**, **⏸ Sleep** (unload the model),
**⟳ Restart**, and **■ Stop** (close TypeWhisper completely) — hover one for what it does. Turn the whole thing on in the control panel (off by default).

Only TypeWhisper's running state changes — your selected model and its settings are untouched.

### When TypeWhisper is in a bad state

Every press checks TypeWhisper before recording. If it isn't running (closed or crashed),
isn't responding (hung), or has no model loaded, Whisper Trigger tells you what's wrong and
fixes it — starting or restarting TypeWhisper — but **never starts recording off the back
of a recovery**: you press again once it reports ready. (Only a sleep Whisper Trigger itself
planned can go straight to recording.) The tray status distinguishes *not running* from
*not responding*, and **Restart** on the control panel's TypeWhisper card forces a fresh start.

A slow start (e.g. the first dictation after a long idle) shows a **STARTING** badge instead
of failing after 5 s. If a start or stop does fail, Whisper Trigger asks TypeWhisper whether
it's actually recording and shows that — the REC badge never hides a live microphone. While
recording, the badge is re-checked every 2 s and clears if TypeWhisper stopped on its own.

### Buttons heal themselves

Windows can silently drop the low-level mouse hook that catches the side buttons (around a
lock/unlock or RDP reconnect). Whisper Trigger re-arms it automatically after every unlock
and reconnect, and a watchdog re-arms it if the pointer moves while the hook hears nothing.
**Reset buttons** on the control panel does the same on demand. Each re-arm is logged.

## Control panel

**Left-click the tray icon** to open the control panel (or launch Whisper Trigger again from the
Start menu while it's running). It shows the current status, a **Start/Stop dictation** button,
the TypeWhisper card (status plus **Wake / Sleep / Restart / Stop**), and every setting —
changes apply immediately, no Save button. Click anywhere else, press Esc, or click the tray
icon again to close it. Right-click the tray icon for a short menu (open, start/stop, exit).

**Recent dictation** sits at the top: your latest dictation (a snippet, when, which app) with a big
**Copy** button, plus the two before it with small copy buttons — for when a paste didn't land
and you'd otherwise dig through TypeWhisper for it. **Open TypeWhisper history ›** opens
TypeWhisper on its Home page (recent transcriptions, then **View all history**); TypeWhisper has
no way to open its History window directly. The list refreshes each time the panel opens.

From the panel you can:

- Remap which side button is **toggle** vs **push-to-talk** (or disable one)
- Change the **auto-stop timeout** (1–60 minutes)
- Turn **Stop recording if focus leaves the target window** on or off (on by default)
- Turn on **Automatically install updates**
- Turn on **TypeWhisper idle sleep**, set its two timers (0 skips a step), and choose what a
  press does while it's asleep
- Turn **Mouse buttons trigger dictation** off (buttons pass through to other apps) and turn
  **Start at login** on or off
- Check for updates, reset the buttons, open the logs folder, or see About

Every setting, including the mouse-buttons switch, is remembered across restarts.

Settings are saved to `%APPDATA%\WhisperTrigger\settings.json`.

## Updates

The app self-updates from GitHub Releases:

- **Check for updates** (control panel) — checks now; when one is ready, click the toast to install.
- **Install updates automatically** (control panel) — when on, updates are applied on launch.

## Notes

- Whisper Trigger does not touch the clipboard — TypeWhisper handles its own clipboard
  save/restore when it pastes. (Note: across a Remote Desktop boundary, RDP clipboard
  redirection can leave the dictated text on the clipboard; that's an RDP limitation.)
- Recording auto-stops after the configured timeout (default 5 minutes) if you forget to
  release/toggle.
- **Focus guard** (on by default): if focus leaves your target window mid-dictation,
  Whisper Trigger stops recording early, returns to the original window, and pastes there;
  if that window is gone, the text is captured in a safe pop-up instead of landing in a
  random app. The early stop is the point — it keeps the transcription from going somewhere
  you didn't intend.
  - The trade-off is that anything which briefly steals focus (a toast, an installer, a
    misbehaving app) cuts your dictation short. If recordings keep ending mid-sentence,
    turn the guard off in the control panel; recording then runs uninterrupted and the text pastes
    into whatever window has focus when you stop.
- Whisper Trigger only *detects and reports* TypeWhisper's state — it never starts,
  installs, or manages TypeWhisper itself.
- Only one instance runs at a time; launching a second copy exits immediately.

## Troubleshooting

### TypeWhisper says "API server enabled, but not running"

The full message looks like:

```
API server enabled, but not running
The process cannot access the file because it is being used by another process.;
cleanup failed: Cannot access a disposed object. Object name: 'System.Net.HttpListener'.
```

Whisper Trigger reports this state but can't fix it — the problem is on the Windows
side, in TypeWhisper's own listener. Despite the wording, **this usually does not mean
another process holds the port.** More often nothing is listening at all.

TypeWhisper's API server is built on `HttpListener`, i.e. HTTP.sys. HTTP.sys refuses to
bind any port that falls inside a Windows *excluded TCP port range*, and reports it as
`ERROR_SHARING_VIOLATION` — the "used by another process" text above. WinNAT (installed
alongside Hyper-V, WSL2, and Docker Desktop) reserves 100-port blocks for NAT at service
start, and if one of those blocks covers TypeWhisper's port, the API server can't start.
The blocks are re-picked on every boot, so this can appear and disappear seemingly at
random.

**Diagnose** — check whether the port is inside an excluded range:

```powershell
netsh int ipv4 show excludedportrange protocol=tcp
```

If TypeWhisper's port (default `8978`) falls in any listed range, that's the cause. Note
that a raw socket test is *not* a valid check here — plain `TcpListener` binds excluded
ports fine, only HTTP.sys refuses them.

**Fix** — the durable fix is to keep WinNAT's blocks out of the low ports by restoring
the Windows default dynamic port range (`49152`–`65535`). In an **elevated** PowerShell:

```powershell
net stop winnat
netsh int ipv4 set dynamicport tcp start=49152 num=16384
net start winnat
```

Reboot, then confirm the API is up (substitute your port if you've changed it):

```powershell
curl.exe http://127.0.0.1:8978/v1/status
```

**Do not** try to fix this with `netsh int ipv4 add excludedportrange` on TypeWhisper's
port. That reserves the port for you, but HTTP.sys still refuses it for exactly the same
reason, so the error is unchanged.

> Stopping WinNAT briefly drops WSL2/Docker networking — restart those containers and
> distros afterwards.

**Or move the port.** Even with the default dynamic range, `8978` can still end up
excluded (a single-port reservation on `8978` has been seen with no `*` marker, i.e. not
added by hand). The simplest dodge is to move TypeWhisper's port (Settings > Advanced >
API Server) to one well below `49152` and outside every listed range — `18978` is a good
choice. Whisper Trigger reads the port from TypeWhisper's discovery file, so it follows
the change automatically; no Whisper Trigger setting needed.

## Developing

Build and run locally (Windows, [.NET 11 SDK](https://dotnet.microsoft.com/download)):

```bash
dotnet run --project src/WhisperTrigger
```

> Note: auto-update only works in an installed build, not a local/dev run.

### Releasing

Releases are built and published automatically by GitHub Actions when you push a
version tag:

```bash
git tag v1.1.1
git push origin v1.1.1
```

The [release workflow](.github/workflows/release.yml) publishes a self-contained build,
packs it with [Velopack](https://velopack.io), and creates a GitHub Release containing the
`WhisperTrigger-win-Setup.exe` installer plus the update packages the running app reads to
update itself. The tag drives the version (`v1.1.1` → `1.1.1`); the `<Version>` in the
`.csproj` is only the default for local builds. No secrets to configure — the workflow uses
the built-in `GITHUB_TOKEN`.
