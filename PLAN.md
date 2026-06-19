# TypeWhisper Mouse Trigger — Planning Document

## Problem

TypeWhisper is running locally while working inside a full-screen Remote Desktop session.

- Manual paste into the remote session works (clipboard transfer is not the issue)
- TypeWhisper's normal keyboard hotkey does **not** trigger when RDP is full-screen
- The keypress is captured and forwarded into the remote session instead of being handled locally
- Changing the hotkey to a single key (e.g. F8) does not help — it is also forwarded into RDP

## Goal

Avoid keyboard hotkeys entirely. Use a physical mouse side button (or other mouse-based input) to trigger TypeWhisper locally.

## Requirements

- Mouse button press is captured **locally** (not forwarded into RDP)
- Local automation calls TypeWhisper's API or command interface directly — does **not** simulate a keyboard shortcut
- TypeWhisper starts or stops dictation in response
- TypeWhisper pastes the final transcription into the currently focused Remote Desktop window

## Desired Flow

```
Mouse side button pressed on local machine
  → local automation captures that mouse button
  → local automation calls TypeWhisper's API / command / workflow trigger
  → TypeWhisper starts or stops dictation
  → TypeWhisper pastes transcription into the focused RDP window
```

## Key Constraint

**Do not rely on keyboard shortcuts.** Full-screen Remote Desktop captures them before the local machine can act on them.

## Open Questions / Investigation Needed — RESOLVED

1. **Does TypeWhisper expose a local API? → YES.** Local REST server on
   `http://127.0.0.1:8978`, including direct dictation start/stop/status. Off by
   default; enable under Settings > Advanced > API Server. Full contract in
   [TYPEWHISPER-API.md](TYPEWHISPER-API.md).
2. **Which capture tool? → Custom C#/Win32 low-level hook (`WH_MOUSE_LL`).** No
   vendor software or AutoHotkey installed; PowerToys is present but can't map a
   mouse *button* to a command. The hook swallows the side button so full-screen
   RDP can't forward it. Built in [src/WhisperTrigger/](src/WhisperTrigger/).
3. **What hardware? → Razer DeathAdder Elite** (`VID_1532&PID_005C`), two side
   buttons emitting `XButton1` (back) / `XButton2` (forward). No Synapse installed.
4. **Trigger without a keypress? → YES.** A `POST /v1/dictation/start` (or `/stop`)
   is exactly that — no simulated keyboard input.

## Implementation

- **Forward button (`XButton2`) → toggle** dictation (start / stop).
- **Back button (`XButton1`) → push-to-talk** (hold to record, release to stop).
- See [README.md](README.md) to build & run.

### Remaining to verify on real hardware — VERIFIED

- API server confirmed working via `curl http://127.0.0.1:8978/v1/status`. ✓
- LL hook confirmed to beat full-screen RDP — side buttons trigger locally. ✓

---

## v1 Feature Set (completed)

The tray app (`src/WhisperTrigger/`) is the shipping artifact. Features:

- **XButton2 (forward)** → toggle dictation on/off
- **XButton1 (back)** → push-to-talk (hold to record, release to stop)
- **Double-click tray icon** → toggle dictation
- **Clipboard save/restore** — captures clipboard before starting, waits for TypeWhisper
  to finish pasting, then restores original clipboard contents
- **Enable/disable hook** — tray menu checkbox; when disabled, side buttons pass through
  to other apps normally (icon becomes an outlined circle); "Toggle dictation" menu item
  is also greyed out when disabled
- **5-minute auto-stop** — recording stops automatically if left running; user is notified
- **Safety stop on startup** — sends a stop call on launch to clear any state left by a previous crash
- **Start at login** — writes/removes `HKCU\...\Run` registry value
- **Single instance** — second launch exits immediately
- **Visual state** — filled gray = idle, filled red = recording, outlined gray = disabled
- **About** — tray menu item shows current version number

## Known Limitations (v1)

- TypeWhisper pastes all transcribed text at once when recording stops. There is no
  word-by-word streaming insertion.

## Future Goals

### Live streaming text insertion

Instead of buffering audio and pasting at the end, stream transcribed words into the
target app in real time as the user speaks. TypeWhisper's Windows app has a floating
overlay preview but does not expose streaming output through its REST API.

To achieve true streaming we would likely need to build our own dictation component
that talks directly to a local Whisper model (e.g. whisper.cpp, SherpaOnnx) or a
streaming-capable cloud provider (e.g. Deepgram, AssemblyAI) and injects text via
`SendInput` or accessibility APIs as words arrive. This is a significant effort and
is deferred post-v1.
