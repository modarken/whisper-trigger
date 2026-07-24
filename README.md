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

Buttons are remappable — see [Settings](#settings). Double-clicking the tray icon also
toggles dictation.

### Recording indicator

A small **REC badge** appears at the top-center of the screen while recording, so you can
tell dictation is live even when the tray icon is hidden behind a full-screen Remote
Desktop window.

### Tray icon states

| Icon | Meaning |
|------|---------|
| ● Gray | Idle — listening for button presses |
| ● Red | Recording |
| ● Amber | TypeWhisper is offline or not installed (dictation can't run) |
| ○ Outlined | Mouse buttons disabled (pass-through to other apps) |

## Settings

Right-click the tray icon → **Settings…** to:

- Remap which side button is **toggle** vs **push-to-talk** (or disable one)
- Change the **auto-stop timeout** (1–60 minutes)
- Turn on **Automatically install updates**

Settings are saved to `%APPDATA%\WhisperTrigger\settings.json`.

## Updates

The app self-updates from GitHub Releases:

- **Check for updates** (tray menu) — checks now; when one is ready, click the toast to install.
- **Automatically install updates** (Settings) — when on, updates are applied on launch.

## Notes

- Whisper Trigger does not touch the clipboard — TypeWhisper handles its own clipboard
  save/restore when it pastes. (Note: across a Remote Desktop boundary, RDP clipboard
  redirection can leave the dictated text on the clipboard; that's an RDP limitation.)
- Recording auto-stops after the configured timeout (default 5 minutes) if you forget to
  release/toggle.
- If focus leaves your target window mid-dictation, Whisper Trigger returns to it before
  pasting; if that window is gone, the text is captured in a safe pop-up instead of landing
  in a random app.
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

Reboot, then confirm the API is up:

```powershell
curl.exe http://127.0.0.1:8978/v1/status
```

**Do not** try to fix this with `netsh int ipv4 add excludedportrange` on TypeWhisper's
port. That reserves the port for you, but HTTP.sys still refuses it for exactly the same
reason, so the error is unchanged.

> Stopping WinNAT briefly drops WSL2/Docker networking — restart those containers and
> distros afterwards.

Prefer the fix above and keep TypeWhisper on its default port — it addresses the cause
rather than working around it, and the dynamic port range setting persists across reboots.
As a last resort (e.g. you can't run elevated), you can instead move TypeWhisper's port
(Settings > Advanced > API Server) to one outside every excluded range; Whisper Trigger
reads the port from the discovery file, so it follows the change automatically. The
downside is a non-default port to remember on every machine and reinstall.

## Developing

Build and run locally (Windows, [.NET 10 SDK](https://dotnet.microsoft.com/download)):

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
