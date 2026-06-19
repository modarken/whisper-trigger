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

## Prerequisites

1. [TypeWhisper](https://www.typewhisper.com) installed and running
2. API server enabled: TypeWhisper → **Settings → Advanced → API Server**

## Mouse buttons

| Button | Action |
|--------|--------|
| Back (XButton1) | **Push-to-talk** — hold to record, release to stop |
| Forward (XButton2) | **Toggle** — press to start, press again to stop |

## Tray icon states

| Icon | Meaning |
|------|---------|
| ● Gray | Idle — listening for button presses |
| ● Red | Recording |
| ○ Outlined | Mouse buttons disabled (pass-through) |

## Releasing

Releases are built and published automatically by GitHub Actions when you push a
version tag:

```bash
git tag v1.0.1
git push origin v1.0.1
```

The [release workflow](.github/workflows/release.yml) publishes a self-contained
build (no .NET install required on the user's machine), packs it with
[Velopack](https://velopack.io), and creates a GitHub Release containing:

- `whisper-trigger-win-Setup.exe` — the installer to hand to users
- Update packages the running app reads on launch to update itself

The tag drives the version (`v1.0.1` → `1.0.1`); the `<Version>` in the `.csproj`
is only the default for local debug builds. No secrets to configure — the workflow
uses the built-in `GITHUB_TOKEN`.

## While recording

A small **REC badge** appears at the top-center of the screen so you can tell dictation
is live even when the tray icon is hidden behind a full-screen Remote Desktop window.

## Settings

Right-click the tray icon → **Settings…** to:

- Remap which side button is **toggle** vs **push-to-talk** (or disable one)
- Change the **auto-stop timeout** (1–60 minutes)
- Turn on **Automatically install updates**

Settings are saved to `%APPDATA%\WhisperTrigger\settings.json`.

## Updates

The app self-updates from GitHub Releases:

- **Automatically install updates** (Settings or tray menu) → updates are applied on launch
- **Check for updates** (tray menu) → checks now; when one is ready, click the toast to install

## Notes

- Clipboard is saved before dictation starts and restored after TypeWhisper finishes pasting (plain text only — images/files on the clipboard are not preserved)
- Recording auto-stops after the configured timeout (default 5 minutes) if you forget to release/toggle
- Right-click the tray icon to open settings, enable/disable buttons, toggle start at login, check for updates, or exit
- Double-click the tray icon to toggle dictation
- Only one instance runs at a time; launching a second copy exits immediately
