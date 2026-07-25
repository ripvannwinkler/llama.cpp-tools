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
  `ctx-size - maxOutputTokens` (output is `8192` by default) to match the
  corresponding `models.ini` section.
- `C:\Users\Chris\.config\opencode\opencode.json` — opencode CLI provider
  config (`provider.llama-local.models`). Each entry's `limit.context` should
  equal the corresponding `models.ini` section's `ctx-size` directly (no
  output subtraction here); `limit.output` should match the same model's
  `maxOutputTokens` in `chatLanguageModels.json`.

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
  becomes healthy/ready) rather than a fast GPU-bound one.
- **CPU-pegging symptom from VS Code chat agent mode (not opencode, not the
  web UI/plain chat)**: any model called with `tools` + `tool_choice: auto`
  can appear to hang (~50% CPU, very slow, but not truly infinite). Root
  cause is upstream, in the vendored `src/` (not a `models.ini` bug):
  - With `tool_choice: auto` (what VS Code sends), llama.cpp's tool-call
    grammar is *lazy* — it only starts constraining generation once the
    model emits the literal trigger string for tool calls (the exact
    detection/trigger logic differs per chat-template family — see
    `common_chat_try_specialized_template` and the per-family
    `common_chat_params_init_*` functions in `src/common/chat.cpp`,
    e.g. `data.grammar_lazy = !(has_response_format || (has_tools &&
    tool_choice == REQUIRED))`). If a model doesn't reliably produce that
    exact trigger, generation runs completely unconstrained.
  - Independently, on every streamed token the server re-parses the
    *entire* accumulated response text from scratch
    (`tools/server/server-task.cpp`
    `server_task_result_cmpl_partial::update` → `common_chat_parse`), which
    is O(n) per token and O(n²) total as the response grows.
  - Combined: an untriggered, rambling response pays quadratic CPU cost
    against the full output-token ceiling. This lives in upstream `src/`
    and would be overwritten by `scripts/update.ps1`'s `git pull` + rebuild
    if hand-patched — do not patch `src/` directly.
  - This was hit by `Gemma-4-31B-it-QAT` (a QAT quant) and
    `Qwen3.6-27B-Fable-Fusion-711-Uncensored-Heretic-MTP` (an abliterated
    fine-tune) — both categories of model tend to reproduce trigger tokens
    less reliably than a stock instruct release (e.g.
    `Qwen3.6-35B-A3B-NVFP4`, unaffected). The 31B was removed entirely
    (`models.ini`, both external configs, and the gguf on disk deleted) as
    it kept hitting this; it was replaced by `Gemma-4-26B-A4B-it-QAT`
    (`unsloth/gemma-4-26B-A4B-it-qat-GGUF`, MoE, 262144 ctx verified to
    load at q8_0 KV on the 5090) with `toolCalling` enabled to see whether
    this checkpoint holds up better — it's also a QAT quant, so watch for
    the same symptom before trusting it in agent mode. If another model
    hits it: disabling `toolCalling` for it in `chatLanguageModels.json`
    (VS Code has a per-model toggle) is the safe lever; do not patch
    `src/` directly.
- **`Ornith-1.0-35B`** (`deepreinforce-ai/Ornith-1.0-35B`, agentic coding
  model, Qwen3.5-MoE-based) — an NVFP4 quant was tried first
  (`s-batman/Ornith-1.0-35B-NVFP4-MTP-GGUF`, Blackwell-native, the 5090's CUDA
  build supports it via `ggml-cuda/mmq-config-blackwell.cuh` +
  `template-instances/mmq-instance-nvfp4.cu`) but the download was cancelled
  before finishing. Settled on Unsloth's premade
  `unsloth/Ornith-1.0-35B-GGUF` `Ornith-1.0-35B-UD-Q4_K_XL.gguf` (22.3GB —
  note "Q4_K_XL" is Unsloth's own mixed-precision naming, not a native
  `llama-quantize` type, so it has to be downloaded premade rather than
  produced locally). Full 262144 ctx at q8_0 KV, same footprint class as
  `Qwen3.6-35B-A3B-NVFP4`. `toolCalling` enabled — untested for the
  CPU-pegging issue above; watch for it since agentic-coding models lean
  heavily on tool calls.
