# TypeWhisper Local HTTP API & CLI — Reference

> How TypeWhisper exposes itself for local automation. This is the basis for the
> mouse-trigger plan in [PLAN.md](PLAN.md) — it lets us start/stop dictation
> **without a keyboard hotkey**, so full-screen RDP can't swallow the trigger.
>
> Sourced from the official docs (June 2026):
> - Windows: <https://github.com/TypeWhisper/typewhisper-win>
> - macOS API: <https://www.typewhisper.com/en/docs/mac/api>
> - macOS CLI: <https://www.typewhisper.com/en/docs/mac/cli>

## TL;DR

TypeWhisper runs a **local REST server on `http://127.0.0.1:8978`**. It is **off by
default** — enable it under **Settings > Advanced > API Server**. Once on, you can
toggle dictation with plain HTTP calls:

```bash
curl -X POST http://127.0.0.1:8978/v1/dictation/start
curl -X POST http://127.0.0.1:8978/v1/dictation/stop
curl       http://127.0.0.1:8978/v1/dictation/status
```

That is the whole mechanism we need: a mouse-button handler fires one of these
POSTs locally; TypeWhisper records, transcribes, and pastes into the focused
window (the RDP session) on its own.

## Server basics

| | |
|---|---|
| Host | `localhost` / `127.0.0.1` (local only) |
| Default port | `8978` (configurable in Settings) |
| Enable | Settings > Advanced > API Server |
| API version prefix | `/v1` |

### Discovery (don't hard-code the port)

On startup the app writes a discovery file. Read this instead of assuming `8978`:

- **Windows:** `%LOCALAPPDATA%\TypeWhisper\api-discovery.json`

```json
{ "version": 1, "port": 8978, "token": "..." }
```

If the file is absent, the API server is not enabled (or the app isn't running).

### Authentication

- **Off by default** for local compatibility — no header needed.
- If turned on (Settings > Advanced > API Server > **Require API Token**):
  - `/v1/status` stays public.
  - Every other route needs one of:
    - `Authorization: Bearer <token>`
    - `X-TypeWhisper-API-Token: <token>`
  - The token is the `token` field from the discovery file.
  - `OPTIONS` preflight returns `204 No Content`.

## Endpoints

### Dictation control ← what the mouse trigger uses

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/dictation/start` | Begin recording |
| POST | `/v1/dictation/stop` | Stop recording (then it transcribes + pastes) |
| GET  | `/v1/dictation/status` | Current recording state |
| GET  | `/v1/dictation/transcription?id=<session-id>` | Poll a dictation result by session id |

For a single physical button we want **toggle** behavior: GET `status`, then POST
`start` or `stop` depending on whether it's currently recording.

### Status / models

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/status` | Engine readiness, model info, capability flags, API version (public) |
| GET | `/v1/models` | Available models and their status |

### File transcription (not needed for the trigger, but available)

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/transcribe` | Multipart or raw audio upload (limit 256 MiB → 413 if larger) |
| POST | `/v1/transcribe/local-file` | Transcribe a file path on the local machine (bypasses upload limit) |

Multipart fields: `language`, `language_hint` (repeatable; mutually exclusive with
`language`), `task` (`transcribe`/`translate`), `target_language`,
`response_format` (`json`/`verbose_json`), `prompt`, `engine`, `model`.
Raw-audio equivalents go in headers: `X-Language`, `X-Language-Hints`, `X-Task`,
`X-Target-Language`, `X-Response-Format`, `X-Prompt`, `X-Engine`, `X-Model`.
Append `?await_download=1` to wait for a local model to restore.

`/v1/transcribe/local-file` body:

```json
{ "path": "C:\\Audio\\recording.wav", "language_hints": ["de","en"], "task": "transcribe" }
```

### History / rules

| Method | Path | Purpose |
|---|---|---|
| GET | `/v1/history?q=<query>&limit=<n>&offset=<n>` | Search history |
| DELETE | `/v1/history?id=<uuid>` | Delete an entry |
| GET | `/v1/rules` | List rules (legacy alias `/v1/profiles`) |
| PUT | `/v1/rules/toggle?id=<uuid>` | Toggle a rule |

### Errors

Standard HTTP codes with JSON error bodies: `400` invalid input, `413` payload too
large, `503` model unavailable, `500` transcription failure.

## CLI (`typewhisper`)

Ships with TypeWhisper Windows; talks to the same local API and auto-reads the
discovery file. Requires the API server enabled. Intended for scripts, terminals,
Raycast, batch workflows.

```bash
typewhisper status
typewhisper models
typewhisper dictation start          # dictation subcommands per Windows repo
typewhisper transcribe recording.wav --language de --json
typewhisper transcribe - < audio.wav
```

Useful flags: `--port <N>`, `--api-token <token>`, `--json`, `--language <code>`,
`--language-hint <code>` (repeatable), `--task`, `--translate-to <code>`,
`--engine <id>`, `--model <id>`, `--await-download`.
Env: `TYPEWHISPER_API_TOKEN` for a persistent token.

> Note: the macOS CLI docs page only lists `status` / `models` / `transcribe`
> explicitly; the Windows repo shows `dictation` subcommands. For the trigger we
> rely on the HTTP endpoints directly, which are documented on both platforms.

## What this means for the mouse trigger

1. **Open Question #1 (does it expose a local API?) → YES.** Local REST on
   `127.0.0.1:8978`, including direct dictation start/stop/status.
2. **Open Question #4 (trigger without a simulated keypress?) → YES.** A POST to
   `/v1/dictation/start` (or `/stop`) is exactly that — no keyboard involved.
3. **Next, before any of this works:** enable Settings > Advanced > API Server,
   then confirm `curl http://127.0.0.1:8978/v1/status` responds.
4. **Still open (#2/#3):** which tool captures the mouse side button *locally*
   before RDP forwards it, and what mouse hardware/software is in use.
