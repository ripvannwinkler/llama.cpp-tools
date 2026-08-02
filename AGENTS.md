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

Several external configs outside this repo duplicate each model's id and
**context size** so that other tools can talk to the same router
(`http://127.0.0.1:8080/v1`). Whenever a model's `ctx-size` (or the model
list itself) changes in `models.ini`, update these too:

- `C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` — VS
  Code chat model list. Each entry's `maxInputTokens` should equal
  `ctx-size - maxOutputTokens` (output is `8192` by default) to match the
  corresponding `models.ini` section.

**Pi no longer needs manual mirroring.** Both pi configs
(`E:\01-personal\pi.dev\models.json` and `C:\Users\Chris\.pi\agent\models.json`)
are now empty (`providers: {}`) — pi's built-in llama.cpp router provider
(built into pi, registered at startup) auto-discovers models from the router at
`LLAMA_BASE_URL` (default `http://127.0.0.1:8080`; the pi.dev compose.yml sets
`host.docker.internal:8080`). It reads each model's id, input modalities, and
ctx-size from the router's catalog (`/models`, `meta.n_ctx` = the preset's
ctx-size), so `models.ini` ctx edits propagate with zero config changes.
Flow: `/llama` (load/unload/download) → `/model` (select). Note it only lists
**loaded** models in `/model`, and it does **not** enable thinking
(`reasoning: false`, `maxTokens = contextWindow`) — the old configs had
`reasoning: true` + `qwen-chat-template` thinking on all models; restore that
per model via `modelOverrides` in the pi `models.json` if needed:
`providers.llama.cpp.modelOverrides["<model-id>"] = { "reasoning": true,
"compat": { "thinkingFormat": "qwen-chat-template" } }`.

Entries key by the same model id used in `models.ini` (the
`[section]` name). Adding, removing, or renaming a model in `models.ini` should
be mirrored in the VS Code list as well, not just context-size edits.

KV cache quant (`cache-type-k`/`cache-type-v`) is router-internal and does
**not** need mirroring here on its own — these configs only track `ctx-size`
and the model list. Only touch them for a KV-quant-only change if it also
changes the max `ctx-size` that fits in VRAM.

## Known-good config notes

- `[*]` global defaults: `n-gpu-layers = 99`, `flash-attn = on`,
  `ctx-size = 8192` fallback.
- Every per-model preset should set an explicit `ubatch-size` — a preset
  missing this falls back to llama.cpp's default (512), which at very large
  `ctx-size` can turn model load into a long CPU-bound stall (high CPU,
  server never becomes healthy/ready) rather than a fast GPU-bound one.
  Bench-verified values: `2048` for the MoE presets (+8.6% prompt
  processing vs 1024 on the 35B NVFP4), `1024` for the dense 27B (2048
  gained only 1.3% there).
- Speculative decoding: only the 27B's gguf ships MTP layers
  (`spec-type = draft-mtp`, 71 -> 146 t/s greedy coding); the 35B NVFP4,
  Ornith, and Gemma-4 quants had them stripped and exit on load if
  `spec-type` is set.
- `cache-reuse = 256` is enabled on the long-context presets and confirmed
  to help: on `Qwen3.6-27B-UD-Q4_K_XL`, re-sending an ~8k-token prompt with
  a couple of tokens prepended (simulating a shifted/edited conversation
  history) reused the entire prior cache (cache_n 8012, prompt_n 4) instead
  of reprocessing from scratch — prompt eval dropped from ~2.4s to ~67ms.
  Verified with both requests pinned to the same slot via `id_slot` (the
  router has multiple slots; an unpinned A/B test can land on different
  slots and look like reuse isn't working when it actually is). The
  earlier note here claiming `--cache-reuse` was unsupported on all
  current models (M-RoPE unshiftable) was wrong, or at least is not true
  for the current dense/MoE text presets — only Qwen3-VL/Qwen2.5-VL (M-RoPE
  vision models) would plausibly still hit that limitation, untested.
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
- **Tray auto-unload false-triggering mid-session** — `LlamaTray`'s idle
  auto-unload (`TrayAppContext.CheckAutoUnloadAsync`) originally judged
  activity purely from `/slots`' `is_processing`, sampled once per 3s poll
  tick. That's a point-in-time snapshot: any request that starts and fully
  completes between two ticks (common for short prompts) is invisible to
  it, so `_lastActivityUtc` could go stale for the whole `AutoUnloadMinutes`
  window even with real, ongoing use — confirmed in `server.err.log`, where
  real request bursts were interleaved with the tray's own 3s-cadence
  `/slots` polling and easily missed by the snapshot check. Fixed by reading
  the cumulative `n_decode_total` counter from each child's `/metrics`
  instead (`ServerController.GetDecodeTotalAsync`) — since it's monotonic,
  any increase between polls proves decoding happened sometime in that
  window, no matter how brief. Requires `metrics = on` in the model's
  preset (added to `[*]` in `models.ini`); `is_processing` is kept as a
  fallback only for when `/metrics` is unavailable. If a model preset
  outside `models.ini` (e.g. a per-model override) doesn't inherit `[*]`,
  auto-unload silently falls back to the old lossy detection for it.
