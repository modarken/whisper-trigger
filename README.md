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

## Notes

- Clipboard is saved before dictation starts and restored after TypeWhisper finishes pasting (plain text only — images/files on the clipboard are not preserved)
- Recording auto-stops after 5 minutes if you forget to release/toggle
- Right-click the tray icon to enable/disable buttons, toggle start at login, or exit
- Double-click the tray icon to toggle dictation
- Only one instance runs at a time; launching a second copy exits immediately
