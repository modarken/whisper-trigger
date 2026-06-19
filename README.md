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

- Clipboard is saved before dictation starts and restored after TypeWhisper finishes
  pasting (plain text only — images/files on the clipboard are not preserved).
- Recording auto-stops after the configured timeout (default 5 minutes) if you forget to
  release/toggle.
- If focus leaves your target window mid-dictation, Whisper Trigger returns to it before
  pasting; if that window is gone, the text is captured in a safe pop-up instead of landing
  in a random app.
- Whisper Trigger only *detects and reports* TypeWhisper's state — it never starts,
  installs, or manages TypeWhisper itself.
- Only one instance runs at a time; launching a second copy exits immediately.

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
