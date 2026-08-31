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
- `llama_tool_eval_all_models_v2.py` + `results__*.csv`/`results__*.json`
  — tool-calling eval harness and its per-model result artifacts.
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
- `server.out.log` / `server.err.log` — router stdout/stderr, next to the
  root AGENTS.md (paths configured in `tray/LlamaTray/appsettings.json`).

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
