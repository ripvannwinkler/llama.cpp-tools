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
- `server.out.log` / `server.err.log` — router stdout/stderr, next to this
  file (paths configured in `tray/LlamaTray/appsettings.json`).

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

`scripts\probe-ctx-headroom.ps1` launches its own server and samples **idle**
VRAM to find the largest `ctx-size` that leaves a requested headroom, using a
temporary preset so each candidate includes the model's mmproj, speculative
drafter, KV types, and other per-model settings. Compute buffers grow during
generation, so an idle probe over-reports free VRAM; pad the headroom target
and re-check `nvidia-smi` during a real generation before treating a context
as final. Re-probe after any change that frees or consumes VRAM (weight size,
KV quant, speculative head, mmproj).

## Related tools — update whenever model params change

Several external configs outside this repo duplicate each model's id and
**context size** so that other tools can talk to the same router
(`http://127.0.0.1:8080/v1`). Whenever a model's `ctx-size` (or the model
list itself) changes in `models.ini`, update these too:

- `C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` — VS
  Code chat model list. Each entry's `maxInputTokens` should equal
  `ctx-size - maxOutputTokens` (output is `8192` by default) to match the
  corresponding `models.ini` section.
- `C:\Users\Chris\.pi\agent\models.json` — pi's `llama-local` provider.
  Each entry under `providers.llama-local.models[]` keys by `id` (the
  `models.ini` section name); `contextWindow` = that section's `ctx-size`.
  `maxTokens` is a deliberate per-tool choice, not derived (currently `65536`
  everywhere) — leave it alone unless asked. `apiKey` is a dummy literal
  (`not-required`): the router runs without `--api-key`, but pi hides a model
  from `/model` unless *some* auth is configured for its provider, so don't
  remove it. `input` (vision) and `reasoning`/`compat.thinkingFormat` are
  **not** auto-derived by the sync skill — set by hand per model, mirroring
  the section's `mmproj` line and its `reasoning = on` flag.

  pi also ships a **built-in** `llama.cpp` provider (`/login llama.cpp`, driven
  by `/llama`) that auto-discovers from the router. It is deliberately unused:
  it lists only *loaded* models and hardcodes `reasoning: false`. Don't
  "simplify" the static `llama-local` list away in favour of it.

Entries key by the same model id used in `models.ini` (the
`[section]` name). Adding, removing, or renaming a model in `models.ini` should
be mirrored in the VS Code list as well, not just context-size edits.

KV cache quant (`cache-type-k`/`cache-type-v`) is router-internal and does
**not** need mirroring here on its own — these configs only track `ctx-size`
and the model list. Only touch them for a KV-quant-only change if it also
changes the max `ctx-size` that fits in VRAM.