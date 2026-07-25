# Instructions for this workspace (root)

This root is Chris's personal local-inference setup built on top of the
vendored `llama.cpp` upstream checkout in `src/` (which has its own
[src/AGENTS.md](src/AGENTS.md) — that one is upstream's contributor policy,
not relevant here). This file documents the *personal* setup: the router
config, the tray app, and the external tools that mirror its model list.

## Layout

- `models.ini` — per-model preset config for `llama-server` router mode
  (`--models-preset`). Section name = model id = folder name under `models/`.
  Precedence: CLI args > `[model-id]` section > `[*]` global default.
- `models/` — one folder per model (gguf + optional mmproj), auto-scanned by
  the router using `--models-dir`.
- `scripts/` — `start-llama.ps1` (launches the router), `stop-llama.ps1`,
  `restart-llama.ps1`, `load.ps1`/`unload-llama.ps1` (per-model load/unload
  via `/models/load`), `bench.ps1`, `update.ps1`, `probe-ctx.ps1`.
- `tray/LlamaTray/` — a Windows tray app (C#) that wraps the same router:
  `ServerController.cs` starts `llama-server.exe` with
  `--models-dir`/`--models-preset`/`--port`/`--host` (no per-model flags —
  those all come from `models.ini`), and polls `/health` and `/models` to
  confirm start/load.
- `server.out.log` / `server.err.log` — router stdout/stderr, next to this
  file (paths configured in `tray/LlamaTray/appsettings.json`).

## Related tools — update whenever model params change

Two external configs outside this repo duplicate each model's id and
**context size** so that other tools can talk to the same router
(`http://127.0.0.1:8080/v1`). Whenever a model's `ctx-size` (or the model
list itself) changes in `models.ini`, update these too:

- `C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` — VS
  Code chat model list. Each entry's `maxInputTokens` should equal
  `ctx-size - maxOutputTokens` (output is consistently `8192` here) to match
  the corresponding `models.ini` section.
- `C:\Users\Chris\.config\opencode\opencode.json` — opencode CLI provider
  config (`provider.llama-local.models`). Each entry's `limit.context` should
  equal the corresponding `models.ini` section's `ctx-size` directly (no
  output subtraction here).

Both key entries by the same model id used in `models.ini` (the `[section]`
name). Adding, removing, or renaming a model in `models.ini` should be
mirrored in both files' model lists as well, not just context-size edits.

## Known-good config notes

- `[*]` global defaults: `n-gpu-layers = 99`, `flash-attn = on`,
  `ctx-size = 8192` fallback.
- Every per-model preset should set an explicit `ubatch-size` (`1024` has
  been the working value across all tuned presets) — a preset missing this
  falls back to llama.cpp's default (512), which at very large `ctx-size`
  can turn model load into a long CPU-bound stall (high CPU, server never
  becomes healthy/ready) rather than a fast GPU-bound one. This bit
  `Gemma-4-31B-it-QAT` in particular: it was reduced from `ctx-size = 262144`
  to `196608` (matching the 27B preset) and given `ubatch-size = 1024` to
  fix a hang where `llama-server` pegged ~50% CPU and never responded to
  `/models/load`.
