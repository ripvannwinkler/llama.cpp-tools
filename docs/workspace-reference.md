# Workspace reference (moved out of AGENTS.md)

Details for the personal setup documented in the root [AGENTS.md](../AGENTS.md).

## Layout

- `models.ini` — per-model preset config for `llama-server` router mode
  (`--models-preset`). Section name = model id = folder name under `models/`.
  Precedence: CLI args > `[model-id]` section > `[*]` global default.
- `models/` — one folder per model (gguf + optional mmproj), auto-scanned by
  the router using `--models-dir`.
- `templates/` — custom chat templates referenced by `chat-template-file`
  in `models.ini` (`Qwen-Sharp-Chat-Template.jinja`,
  `Gemma31b_fixed_chat_template.jinja`, plus older ones).
- `drafters/` — speculative draft models (currently `Qwen3.8-27B-DFlash2`),
  loaded through `spec-draft-model` in `models.ini`. Kept out of `models/`,
  which the router auto-scans into servable presets.
- `mmproj/` — vision projectors not attached to a model folder (currently
  `mmproj-BF16-qwen38_27b.gguf`, whose `mmproj` line in `models.ini` is
  commented out). Also outside `models/`; note some projectors do live inside
  their model folder, so check both places when a preset's `mmproj` is edited.
- `merge/` — one-off `download_and_merge.py` plus its own pip-tools venv
  (`requirements.in` / `requirements.txt`), used to merge an upstream HF repo
  ahead of GGUF conversion. Not part of the running server.
- `scripts/`:
  - `start-llama.ps1` / `stop-llama.ps1` / `restart-llama.ps1` — launch, stop,
    and restart the router. Neither launcher passes per-model flags or
    `--parallel`; everything per-model comes from `models.ini`.
  - `load.ps1` / `unload-llama.ps1` — load/unload a specific model via
    `/models/load`.
  - `bench.ps1` — `llama-bench` wrapper (ignores all `spec-*` settings).
  - `bench-spec.ps1` / `bench-dflash2.ps1` — server-side speculative-decoding
    benchmarks, the only ones that see `spec-*`; measure tok/s from the
    `timings` object on `/v1/chat/completions` responses.
  - `bench-27b-probe.ps1` — quick decode probe against the running router
    (`-Name` fuzzy-matches a model, `-Runs`/`-MaxTokens`); prints per-run
    tok/s, draft acceptance, and tokens per main step.
  - `probe-ctx-headroom.ps1` — finds the largest `ctx-size` that leaves a
    requested VRAM headroom using a temporary router preset (includes each
    model's mmproj, drafter, and KV types); `probe-ctx.ps1` — simpler probe
    that does not represent the complete preset.
  - `update.ps1` — rebuild/update the vendored `llama.cpp` checkout in `src/`.
- `tray/LlamaTray/` — a Windows tray app (C#) that wraps the same router:
  `ServerController.cs` starts `llama-server.exe` with
  `--models-dir`/`--models-preset`/`--port`/`--host` (no per-model flags —
  those all come from `models.ini`), and polls `/health` and `/models` to
  confirm start/load.
- Log file: there is one, not a stdout/stderr pair. `start-llama.ps1` /
  `restart-llama.ps1` pass llama.cpp's `--log-file` (default
  `D:\llama.cpp\server.err.log`). The tray app captures the server's
  stdout/stderr into a unique per-launch temp file
  (`%TEMP%\llama-server-tray-<timestamp>-<random>.log`) instead of the
  configured `LogFile`, because truncating a file another tool has open fails;
  `ServerController.ActiveLogFile` exposes it and the View Log dialog follows
  it (re-pointing on each start). The configured `LogFile` (from
  `appsettings.json` layered with `publish/appsettings.local.json`)
  is only the fallback when the server wasn't started by the tray.
  `server.err.log`/`server.log` hold script- and older tray-launched runs;
  `server.out.log` is dead: `ServerConfig.cs` still names it
  `LegacyStdOutLog` and nothing writes it.
- Router process model: the port 8080 router spawns a separate
  `llama-server.exe` worker per loaded model on a dynamic localhost port
  (`--models-max 1` right now, so one worker at a time), and proxies OpenAI
  requests to it. Two `llama-server` processes in the task list is normal.
  Log lines are prefixed with the serving port, and the leading timestamp is
  `minutes.seconds.millis.micros` since that process started.

## Downloading models

Use `uvx hf download <repo> <file> --local-dir D:\llama.cpp\models\<model-id>`
rather than raw `curl` — a bare `hf` is not on PATH (no global Python/pip
install of the `huggingface_hub` CLI), so it must be run via `uvx hf ...`
(uv fetches/caches the tool on first use). `HF_HOME` is already set to
`d:\.huggingface`, so it picks up the cache and the configured token
(faster/authenticated where that matters, e.g. gated repos) automatically
with no extra flags. Verify the finished file's size against the repo's
authoritative `Content-Length` (`curl -sIL <url>`) regardless of which method
downloaded it — don't trust a clean exit code alone, a dropped connection can
leave a silently truncated file.

## Context sizing

`scripts/probe-ctx-headroom.ps1` launches its own server and samples **idle**
VRAM to find the largest `ctx-size` that leaves a requested headroom, using a
temporary preset so each candidate includes the model's mmproj, speculative
drafter, KV types, and other per-model settings. Compute buffers grow during
generation, so an idle probe over-reports free VRAM; pad the headroom target
and re-check `nvidia-smi` during a real generation before treating a context
as final. Re-probe after any change that frees or consumes VRAM (weight size,
KV quant, speculative head, mmproj).
