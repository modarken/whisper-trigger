# Whisper Trigger

Trigger [TypeWhisper](https://www.typewhisper.com) dictation via mouse side buttons — works even when full-screen Remote Desktop captures all keyboard input.

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

## Notes

- Clipboard is saved before dictation starts and restored after TypeWhisper finishes pasting (plain text only — images/files on the clipboard are not preserved)
- Recording auto-stops after 5 minutes if you forget to release/toggle
- Right-click the tray icon to enable/disable buttons, toggle start at login, or exit
- Double-click the tray icon to toggle dictation
- Only one instance runs at a time; launching a second copy exits immediately
