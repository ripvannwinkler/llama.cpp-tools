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
  in `models.ini` (`Qwen3-Fixed-Chat-Template.jinja`,
  `Gemma31b_fixed_chat_template.jinja`, plus older ones).
- `llama_tool_eval_all_models_v2.py` + `results__*.csv`/`results__*.json`
  — tool-calling eval harness and its per-model result artifacts.
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

## Related tools — update whenever model params change

Several external configs outside this repo duplicate each model's id and
**context size** so that other tools can talk to the same router
(`http://127.0.0.1:8080/v1`). Whenever a model's `ctx-size` (or the model
list itself) changes in `models.ini`, update these too:

- `C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` — VS
  Code chat model list. Each entry's `maxInputTokens` should equal
  `ctx-size - maxOutputTokens` (output is `8192` by default) to match the
  corresponding `models.ini` section.
- `C:\Users\Chris\.config\opencode\opencode.json` — opencode's `llama-local`
  provider. Each entry under `provider.llama-local.models` keys by the same
  `models.ini` section name; `limit.context` = that section's `ctx-size`.
  `limit.output` is a deliberate per-tool choice, not derived (currently
  `8192` everywhere) — leave it alone unless asked.
- `C:\Users\Chris\.pi\agent\models.json` — pi's `llama-local` provider.
  Each entry under `providers.llama-local.models[]` keys by `id` (the
  `models.ini` section name); `contextWindow` = that section's `ctx-size`.
  `maxTokens` is likewise a deliberate choice (`8192` everywhere), not
  derived. `input` (vision) and `reasoning`/`compat.thinkingFormat` are
  **not** auto-derived by the sync skill — set by hand per model, mirroring
  whatever `chatLanguageModels.json`'s `vision` flag already says for that
  model, and `reasoning: true` + `qwen-chat-template` only for `models.ini`
  presets with `reasoning = on`.

Entries key by the same model id used in `models.ini` (the
`[section]` name). Adding, removing, or renaming a model in `models.ini` should
be mirrored in the VS Code list as well, not just context-size edits.

KV cache quant (`cache-type-k`/`cache-type-v`) is router-internal and does
**not** need mirroring here on its own — these configs only track `ctx-size`
and the model list. Only touch them for a KV-quant-only change if it also
changes the max `ctx-size` that fits in VRAM.

## Known-good config notes

- `[*]` global defaults: `n-gpu-layers = auto`, `flash-attn = on`,
  `ctx-size = 8192` fallback, `temp 0.6`, `min-p 0.0`. Every per-model
  preset pins `n-gpu-layers = 999`.
- Every per-model preset should set an explicit `ubatch-size` — a preset
  missing this falls back to llama.cpp's default (512), which at very large
  `ctx-size` can turn model load into a long CPU-bound stall (high CPU,
  server never becomes healthy/ready) rather than a fast GPU-bound one.
  Bench-verified values: `2048` for the MoE 35B (+8.6% prompt processing
  vs 1024) and for Muse-Glimmer; `1024` for Gemma-4-31B. The dense 27B
  bench found 2048 gained only 1.3% over 1024, but the 27B preset is now
  on `2048` anyway (raised along with `batch-size 2048`).
- **Sampling params** — every preset now states its full sampler set explicitly
  rather than inheriting llama.cpp's defaults (`src/common/common.h`: `temp 0.8`,
  `top_k 40`, `top_p 0.95`, `min_p 0.05`), which match no model card used here.
  Convention: the Qwen3.6-35B MoE preset (Qwen3.5-MoE-based) gets `temp 0.6` / `top-k 20` /
  `top-p 0.95` / `min-p 0.0` — Qwen's published thinking-mode values. Gemma-4
  gets `top-k 64` and `temp 0.7` (the card says `1.0`; `0.7` is a deliberate
  middle ground for agentic coding). Muse-Glimmer is the least grounded — `0.8` /
  `40` / `0.95` / `0.0`, explicit-and-conservative rather than card-derived.
  **Do not pin a `reasoning = on` preset near-greedy** (the Qwen3.6 presets and
  the Gemma QAT-abliterated one were previously at `temp = 0.1`): Qwen explicitly
  warns that near-greedy thinking-mode decoding causes endless repetition, and
  it's a plausible contributor to the CPU-pegging symptom below, since a model
  locked into a non-terminating pattern never emits the tool-call trigger the
  lazy grammar waits for. `[*]` carries `temp 0.6` / `min-p 0.0` so a newly added
  preset can't silently land on `0.8` / `0.05`.
  Client override: `opencode.json` sets per-agent `temperature` (currently `0.6`
  for both `plan` and `build`), which **overrides** the preset for opencode
  sessions; VS Code chat and pi send no temperature, so the preset governs there.
  These sampler values are not part of the `ctx-size` mirroring contract above —
  the external configs don't carry them.
- Speculative decoding: the 27B's gguf ships MTP layers built-in
  (`spec-type = draft-mtp`; `qwen35.nextn_predict_layers = 1` +
  `blk.64.nextn.*` tensors, so no `spec-draft-model` sidecar is needed —
  71 -> 146 t/s greedy coding on the older NVFP4 quant). The older
  `Qwen3.6-35B-A3B-NVFP4` quant and the since-removed Ornith exit on load
  if `spec-type` is set (MTP was stripped in those quants); the current
  `Qwen3.6-35B-A3B-MXFP4_MOE-BF16` file ships MTP built-in (the `-MTP-`
  in the filename), so that preset runs `spec-type = draft-mtp` with no
  sidecar drafter. The unsloth Gemma-4-31B-it release shipped a separate
  `mtp-gemma-4-31B-it.gguf` drafter (Q8_0) that works with any quant of
  the same model — configure via `spec-draft-model` + `spec-type = draft-mtp`
  + `spec-draft-n-max 4` (70 -> 101 tok/s on Q4_K_M); the current
  QAT-Abliterated slot instead uses the repo's own
  `mtp-ggml-model-bf16.gguf`.
- **KV-cache quant, not MTP, is what costs gen speed in the deep tail on
  `Qwen3.8-27B-UD-Q4_K_XL`.** Draft acceptance held 79-86% across every
  variant benched, so a slow-at-depth reading is a KV/VRAM problem — check
  those before touching `spec-*`.
  Matched A/B at `ctx-size 131072`, `spec-draft-n-max 4`, `p-min 0.5`,
  400-tok gen, **3 reps** (mean, range):

  | depth | `q8_0/q8_0` | `f16/f16` |
  |---|---|---|
  | ~0    | 127 (118-139) | 120 (101-143) |
  | 57.6k | 104 (96-109)  | 112 (97-132)  |
  | 63.4k | **75 (72-78)**  | **95 (92-99)**  |

  Only the 63.4k row separates cleanly (f16 +27%); at <=57k the gap is <=8%
  with overlapping ranges, i.e. noise at 3 reps. Short-gen runs vary +/-10%
  because draft acceptance swings 58-78% run to run — **do not tune off
  single runs at shallow depth**, which an earlier version of this note did.
  VRAM: `q8_0/q8_0` 27.1 GB vs `f16/f16` 30.3 GB of 32.6 (both at 131072 with
  the vision tower loaded). `q8_0/q8_0` is the reasonable swap if that ~3 GB of
  headroom is needed back (browser/game running), costing ~21% only past ~60k.
  Separately, `q8_0/q8_0 @262k` measured 51 t/s at 63k — that is **VRAM
  spill**, not dequant cost: it puts VRAM at 31.7/32.6 GB where WDDM falls
  back to shared host memory instead of OOM-ing. Same KV types at 131k give
  75. Don't read that 51 as the price of q8.
  `spec-draft-p-min`: `0.75` cut mean draft length to ~2 of the allowed 4;
  `0.5` drafts more, accepts a lower fraction, and nets faster.
  `spec-draft-n-max 6` beat 4 only at shallow depth and lost at 57k, so 4 stays.
- **Max ctx for `Qwen3.8-27B-UD-Q4_K_XL` at f16 KV with no vision tower is
  163840** (settled value). Load-only VRAM probe on the 32.6 GB 5090, f16/f16,
  no mmproj — `139264` 28.7 GB | `147456` 29.3 GB | `155648` 29.9 GB |
  **`163840` 30.6 GB** | `172032` 31.1 GB | `180224` 31.9 GB | `188416` 32.1 GB
  | `196608` 32.0 GB (595 MiB free, shared climbing) | `229376`+ fails to
  create the context. Above 163840 headroom collapses for little gain (172032
  costs 576 MiB for +8k tokens), and the **idle desktop baseline alone swings
  0.8-2.6 GB**, so anything past ~164k spills to shared memory the moment a
  browser opens. Verified at 163840 on the live router: ~101 t/s at 61k depth,
  **73 t/s at ~161k (near-ceiling)**, VRAM steady at 30.9 GB, no collapse.
- **f16 KV is the right default on every preset here** — the same A/B was run
  on all four (3 reps/depth, true depth via `/tokenize`, not `prompt_n`). f16
  beat the previous quantized KV at every model's deepest measured point:

  | model | before (kv, ctx, spec) | after | gen t/s before -> after |
  |---|---|---|---|
  | Qwen3.8-27B  | q8_0/q4_0 262144 n4 p.75 | f16 163840 n4 p.5 | 72 -> 97 @63k |
  | Qwen3.6-35B  | q8_0/q4_0 262144 n2 p.75 | f16 212992 n4 p.5 | 95 -> 134 @192k |
  | Gemma-4-31B  | q8_0/q4_0 131072 n3 p.75 | f16 73728 n4 p.5  | 74 -> 97 @~60k |
  | Muse-Glimmer | q8_0/q8_0 262144 n4 p.6  | f16 262144 n4 p.6 | 80 -> 93 @192k |

  Sizing rule used throughout: pick the largest `ctx-size` that leaves **~2 GB
  VRAM free** at load. That is not arbitrary — the idle desktop baseline alone
  swings 0.8-2.6 GB, and WDDM silently spills to shared host memory instead of
  OOM-ing, so a preset tuned to <1 GB free collapses the moment a browser
  opens. Measured VRAM per model is in the per-model notes below.
- **KV cost per token varies hugely by arch — always probe, never extrapolate
  from another model.** `Qwen3.6-35B` is MoE + hybrid SSM (41 blocks,
  `full_attention_interval 4`, kv heads 2 x 256) so f16 KV is cheap: it holds
  212992 in 30.6 GB. `Muse-Glimmer` is cheaper still (3-in-4 sliding window
  2048, kv heads 2 x 128) — f16 at the full 262144 costs only 28.7 GB, so it
  lost no context at all. `Gemma-4` is the opposite: `key_length`/
  `value_length` **512** with 1-in-6 full attention costs ~78 MiB per 1k
  tokens of f16 KV, so 131072 does not fit and it had to drop to **73728**.
  That is the one real regression here — Gemma trades 44% of its context for
  ~32% more speed. Revert it to `q8_0/q4_0 @131072` if long-context Gemma
  matters more than throughput.
- Muse-Glimmer also loads at `327680` (29.8 GB), but its preset yarn-scales
  from a native 131072 by `rope-scale 2` = 262144. Going past that would
  exceed the configured yarn target, so ctx was left at 262144 deliberately —
  it is a quality ceiling, not a VRAM one.
- Speculative retune: `spec-draft-n-max 4` + `spec-draft-p-min 0.5` won on
  Qwen3.8-27B, Qwen3.6-35B and Gemma-4 (on the 35B: 137/117/95 -> 158/150/134
  t/s at 31k/126k/192k, the single biggest win of this pass). Muse-Glimmer
  kept `n-max 4 / p-min 0.6` — `n6/p0.5` was better at 63k but worse at 192k
  with overlapping ranges, i.e. not a real win. Note `n-max` costs VRAM
  (draft batch buffers): on the 35B, raising 2 -> 4 added ~425 MiB, which is
  why its ctx is 212992 and not the 229376 that fit at `n-max 2`.
- **Dropping the `mmproj =` line does NOT disable vision.** The router
  auto-discovers any `*.gguf` in the model dir whose filename contains
  "mmproj" (`common/preset.cpp` `is_mmproj_file`) and re-adds `--mmproj` to the
  child argv — confirmed by reading the resolved args from `/models`. To
  actually free that VRAM, rename the file (here:
  `mmproj-BF16.gguf` -> `mmproj-BF16.gguf.old`), or pass `--no-mmproj`.
  Always confirm via `/models` -> `status.args` rather than assuming the
  preset edit took.
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
    fine-tune; **no longer present** — replaced by the stock
    `Qwen3.8-27B-NVFP4` release, which smoke-tests clean on tool calling) —
    both categories of model tend to reproduce trigger tokens
    less reliably than a stock instruct release (e.g. the current
    `Qwen3.6-35B-A3B-MXFP4_MOE-BF16` slot, unaffected). The original unsloth 31B QAT was
    removed (`models.ini`, both external configs, and the gguf on disk
    deleted) as it kept hitting this; it was replaced by
    `Gemma-4-26B-A4B-it-QAT`
    (`unsloth/gemma-4-26B-A4B-it-qat-GGUF`, MoE, 262144 ctx verified to
    load at q8_0 KV on the 5090) with `toolCalling` enabled to see whether
    this checkpoint holds up better — it's also a QAT quant, so watch for
    the same symptom before trusting it in agent mode. The 31B later came
    back as `Gemma-4-31B-IT-NVFP4` (stock instruct, unaffected), which has
    since been replaced by `Gemma-4-31B-it-QAT-Abliterated`
    (`huihui-ai/Huihui-gemma-4-31B-it-qat-q4_0-unquantized-abliterated-GGUF`,
    Q4_K quant of the QAT q4_0-unquantized base, abliterated; configured
    with `mmproj-model-bf16.gguf` vision tower, `mtp-ggml-model-bf16.gguf`
    drafter + `spec-type = draft-mtp` `spec-draft-n-max 4`,
    ctx-size 73728 — f16 KV cap, gemma4's 512-d KV can't fit 131072, see
    the f16 KV note above — cache-reuse 256, same custom chat template) — note it
    straddles BOTH flagged categories (QAT quant + abliterated fine-tune),
    so it is the highest-priority watch for the CPU-pegging symptom;
    `toolCalling` is enabled. If another model
    hits it: disabling `toolCalling` for it in `chatLanguageModels.json`
    (VS Code has a per-model toggle) is the safe lever; do not patch
    `src/` directly.
- **`Qwen3.8-27B-UD-Q4_K_XL`** (originally `unsloth/Qwen3.8-27B-NVFP4`,
  `Qwen3.8-27B-NVFP4.gguf`, 21.6GiB; switched 2026-08-16 to
  `unsloth/Qwen3.8-27B-GGUF`'s `Qwen3.8-27B-UD-Q4_K_XL.gguf`, 16.68GiB — see
  the re-tune note below) — the dense 27B Qwen3.8 slot.
  Stock Qwen instruct release, *not* an abliterated fine-tune, which is the
  main reason for the selection (see the CPU-pegging note above). Arch is `qwen35`
  (`Qwen3_5ForConditionalGeneration` upstream), a hybrid-attention design —
  64 text layers, 48 linear-attention + 16 full in a repeating 3+1 pattern —
  already supported by the vendored `src/`, no `update.ps1` needed.
  - **MTP ships in this quant** despite Unsloth publishing no separate drafter
    gguf. Confirmed by reading the file's own metadata rather than guessing:
    `qwen35.block_count = 65` (64 text layers + 1 MTP layer),
    `qwen35.nextn_predict_layers = 1`, and four `blk.64.nextn.*` tensors
    (`eh_proj`, `enorm`, `hnorm`, `shared_head_norm`). So `spec-type =
     draft-mtp` + `spec-draft-n-max 2` + `spec-draft-p-min 0.6` works with no
     `spec-draft-model` — `/slots?model=Qwen3.8-27B-NVFP4` reports `speculative:
     true`, draft acceptance **88.3%** (634/718), ~108-120 tok/s decode.
     Note `/slots` now 400s without a `?model=` query param.
   - **Stale doc note corrected (now moot post-switch)**: at the time this was
     written no `mmproj` key was present in this preset in `models.ini` — it
     was text-only. (An earlier version of this note claimed vision was wired
     in via `mmproj-Qwen3.8-27B-NVFP4.gguf`, which never existed in the
     config.) Vision was added for real after the quant switch below.
  - `temp = 1.0` is a **deliberate exception** to the repo's `0.6` Qwen
    convention — it is the value Qwen publishes for this model's thinking
    mode. Do not "fix" it to 0.6 for consistency.
  - No `chat-template-file`: the embedded template renders and parses
    correctly as-is (`reasoning_content` populates separately from `content`).
    Tool calling (`tools` + `tool_choice: auto`) returns a correct call in
    ~1s with no sign of the CPU-pegging symptom; `toolCalling` enabled.
  - **Max context re-tune, then quant switch (2026-08-16)**: native
    `n_ctx_train` is actually **262144**, not the 200000 previously
    configured. First pushed `ctx-size` to 262144 on the NVFP4 quant: at the
    repo's usual `cache-type-k q8_0`/`v q4_0` it fit but left only ~0.8GiB
    headroom (31.8/32.6GiB); dropping K to `q4_0` too freed ~2.3GiB headroom
    for the same context. Then re-swept `spec-draft-n-max`/`p-min` on NVFP4
    at 262144 (previous **88.3%** figure above was measured at the old
    `ctx-size 200000`/`n-max 2`/`p-min 0.6`, not reproduced at full ctx):
    `n-max 2/p-min 0.6` → 72.8% accept, 61.7 tok/s; `2/0.75` → 86.9%, 62.0
    tok/s; `4/0.6` → 57.2%, 60.0 tok/s; `4/0.75` → 76.7%, **64.0 tok/s**
    (best NVFP4 result).
  - **Then switched quant entirely to `Qwen3.8-27B-UD-Q4_K_XL`**, after a
    real `llama-bench` head-to-head against the NVFP4 file (`-ngl 999 -fa 1
    -ctk q4_0 -ctv q4_0`, 3 runs): NVFP4 pp512 **4373.6 t/s** / tg128 54.6
    t/s vs Q4_K_XL pp512 3584.5 t/s / tg128 **60.1 t/s**. Root cause of the
    split: reading the NVFP4 gguf's own tensor metadata shows only 168 of
    1202 tensors are actually `nvfp4` — 233 are `q8_0` and 105 are `bf16` —
    so the file is a mixed-precision quant where Blackwell's native FP4
    tensor cores (`BLACKWELL_MMA_AVAILABLE` in
    `ggml-cuda/common.cuh`/`vecdotq.cuh`) only accelerate a fraction of the
    model (likely the FFN matmuls, which explains the pp win); tg is
    bandwidth-bound, where Q4_K_XL's ~5GiB-smaller footprint wins instead.
    Confirmed `Qwen3.8-27B-UD-Q4_K_XL.gguf` retains the same MTP `nextn`
    tensors (`blk.64.nextn.*`) before switching, so speculative decoding
    parity was verified, not assumed. At `ctx-size 262144`,
    `cache-type-k q8_0`/`v q4_0` (back to the repo's standard KV precision —
    the smaller weights left *more* headroom than NVFP4 even at its lower-
    precision `q4_0`/`q4_0`: ~3.14GiB vs ~2.3GiB), a real request measured
    **69.98 tok/s** decode at 83.4% draft acceptance (201/241) —
    better than every NVFP4 configuration tested. Also picked up
    `mmproj-BF16.gguf` (887MiB) from the same repo for vision, which the
    NVFP4 quant never had; with vision loaded, headroom drops to ~2.03GiB
    (30.53/32.6GiB) — still comfortable. A real vision request correctly
    described a test image (red-to-black gradient) end-to-end. Old NVFP4
    model folder deleted after the new config was confirmed working.
    `spec-draft-n-max 4`/`p-min 0.75` carried over unchanged from the NVFP4
    tuning above — already strong on the new quant, not re-swept at the
    time (the f16 KV pass later set `p-min 0.5`). Current preset state:
    f16 KV @ 163840, `p-min 0.5`, vision off (see the `mmproj` note above
    and the models.ini comments), `batch-size`/`ubatch-size 2048`.
- **Reasoning effort (`Qwen3.8-27B-UD-Q4_K_XL` only, and the trap that comes with it)** —
  this is the first preset here to use `chat-template-kwargs`. The model's
  template accepts `reasoning_effort` in **`low` / `medium` / `xhigh`**,
  defaulting to `xhigh`, plus `enable_thinking` and `preserve_thinking`
  (so `reasoning-preserve = on` is genuinely honoured here, not a no-op).
  - **The OpenAI-standard top-level `reasoning_effort` field does not work.**
    `tools/server/server-common.cpp` handles it only for the value `"none"`
    (which just disables thinking); every other value is silently discarded —
    *"other reasoning_effort values are model-specific and not yet handled"*.
    A client sending `"reasoning_effort": "medium"` gets the template default
    with no error, so this fails **silently**, not loudly.
  - The working path is `chat_template_kwargs`, forwarded verbatim into the
    template: `"chat_template_kwargs": {"reasoning_effort": "low"}` per
    request, or the preset default via `chat-template-kwargs` in `models.ini`.
    Verified end-to-end at `temperature 0` on a proof-style prompt:
    reasoning trace 1000 -> 1304 -> 1616 chars for low -> medium -> xhigh.
    (On a *trivial* prompt the three levels do not separate and can even
    invert — don't use an easy question to test this.) Confirming the silent
    drop: a top-level `reasoning_effort: "low"` produced a trace byte-identical
    to explicit `medium`, i.e. it fell through to the preset default.
  - Preset explicitly sets `medium` (the template's own default is `xhigh`;
    the kwarg exists precisely to override it deliberately).
    `reasoning-budget` (`8192` here, `4096` on Qwen3.6) was removed
    repo-wide (2026-08-16) — it's a sampler that forces the end-of-thinking
    tag once a token count is hit
    (`common/reasoning-budget.cpp`), **not** a separate memory allocation:
    reasoning tokens are generated and cached exactly like any other output
    token, so they were always coming out of the same `ctx-size`/`max_tokens`
    budget as everything else, not some extra pool on top of it. Removing
    the cap just lets a model think as long as it wants within that shared
    budget; `xhigh` traces running long is still a real risk of eating into
    the visible answer's token budget (`finish_reason: "length"` with empty
    `content`), it just isn't mitigated by `reasoning-budget` anymore — watch
    `max_tokens` per request instead.
    (`reasoning-budget` accepts `-1` unrestricted / `0` immediate end / `N>0`
    token budget — `common/arg.cpp` — for reference, if ever reintroduced.)
- **`Qwen-Sharp-Chat-Template.jinja` installed on both Qwen presets
  (2026-08-18)** — downloaded from the community HF repo
  `peculiar-ragdoll/Qwen-Sharp-Chat-Templates` (`chat_template.jinja`,
  based on froggeric's Qwen3.8 template, version tag
  `qwen3.8-froggeric-v22.1`). Replaced `Qwen3-Fixed-Chat-Template.jinja` on
  the 35B MoE preset and newly wired a `chat-template-file` into the 27B
  preset (which previously relied on its embedded template). Unlike the
  old "Fixed" templates, which were bugfix-only, this one also **injects an
  opinionated terseness system prompt** ("Answer directly, after
  thinking...") whenever the request has no explicit system message —
  a deliberate behavior change, not just a parsing fix. It also handles
  `reasoning_effort` (low/medium/xhigh) and JSON/XML tool-call formatting,
  same as before. The old `Qwen3-Fixed-Chat-Template.jinja` file was left
  on disk (unreferenced) for rollback. If either Qwen preset starts
  misbehaving on tool calls or reasoning parsing, this template swap is the
  first thing to suspect/revert.
- **`Qwen3.6-35B-A3B-MXFP4_MOE-BF16`** (`Qwen3.6-35B-A3B-MTP-MXFP4_MOE_BF16.gguf`) —
  the 35B MoE slot, replacing the older `Qwen3.6-35B-A3B-NVFP4` quant. The
  current file ships MTP built-in (the `-MTP-` in the filename), so
  `spec-type = draft-mtp` runs with no sidecar drafter. `ctx-size 212992`
  at f16 KV — the largest ctx leaving ~2 GB headroom; `spec-draft-n-max 4`'
  s ~425 MiB of draft buffers is why it's not 229376. Custom
  `Qwen3-Fixed-Chat-Template.jinja`, `reasoning = on` +
  `reasoning-format = deepseek` + `reasoning-preserve = on`, samplers
  `temp 0.6` / `top-k 20` / `top-p 0.95` / `min-p 0.0`.
- **`Ornith-1.0-35B`** — **no longer present** (removed from `models.ini`
  2026-08-16; model folder deleted; a second `Ornith-1.0-35B-MTP-APEX-I-Quality`
  preset existed 2026-08-13 -> 08-16). Was: `deepreinforce-ai/Ornith-1.0-35B`,
  agentic coding model, Qwen3.5-MoE-based. An NVFP4 quant was tried first
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
  preset. It was added to `[*]` for this fix, but `metrics` was removed
  from `[*]` again on 2026-08-10 (`e8345f0`), so the current presets do
  NOT expose `/metrics` and auto-unload silently runs on the old lossy
  `is_processing` fallback for every model. Re-adding `metrics = on` to
  `[*]` restores the monotonic-counter detection if false-triggering
  comes back.
- **`Muse-Glimmer-30B-UD-Q4_K_XL`** (`unsloth/Muse-Glimmer-30B-GGUF`,
  `Muse-Glimmer-30B-UD-Q4_K_XL.gguf`, 14.79GiB) — replaced the abliterated
  `Muse-Glimmer-30B-Abliterated-Q5_K_M` slot with unsloth's stock (non-
  abliterated) release, this time with vision wired in. Unlike the
  Q6_K_XL note below, the much smaller Q4_K_XL weights left enough VRAM
  headroom to add `mmproj-Muse-Glimmer-30B-Q8_0.gguf` (1.91GiB) alongside the
  same-family `dflash-kquant.gguf` drafter (1.52GiB) and still hit the full
  `n_ctx_train` of 131072: real load (`load.ps1`, not just `probe-ctx.ps1`,
  which doesn't account for `mmproj`/drafter overhead) used 26.1GiB at
  `cache-type-v q4_0`, bumped to `cache-type-k/v q8_0`/`q8_0` for better KV
  quality at only +191MiB more (26.3GiB total, ~6.1GiB headroom on the 5090's
  32607MiB) — that small a delta from doubling V-cache precision suggests
  this arch's V-cache is a small fraction of total KV memory here.
  `spec-draft-n-max 4` / `spec-draft-p-min 0.6` (carried over from the
  Q6_K_XL note's bench-verified values for this same `dflash-kquant.gguf`
  file) confirmed ~85% draft acceptance (93/110) in a real request, matching
  the ~80-90% range seen there. The embedded chat template rendered/parsed
  cleanly with no `chat-template-file` override needed (unlike the old
  abliterated entry, which required a custom `muse_glimmer_chat_template.jinja`)
  — `reasoning_content`/`content` split correctly in a real request. A real
  vision request (base64 image + text) round-tripped without error
  (`load_model: loaded multimodal model` in the log), confirming the
  perception encoder loads and processes images end-to-end. Adding `mmproj`
  has a cost: `cache_reuse is not supported by multimodal, it will be
  disabled` — logged at load despite `cache-reuse = 256` still being set in
  the preset, so prompt-prefix reuse no longer applies to this model. A
  `special_eot_id is not in special_eog_ids` warning appears at load; harmless
  in the smoke tests run so far (`finish_reason: "stop"` in both the text and
  vision requests) but worth watching if generation ever fails to terminate.
  Old model folder (`Muse-Glimmer-30B-Abliterated-Q5_K_M/`, gguf + F16 dflash
  drafter + an mmproj that was downloaded but never wired into `models.ini`)
  deleted after the new preset was confirmed working.
  - **YaRN past training context does not work through `llama-server`** —
    tried `ctx-size 262144` + `rope-scaling yarn` + `rope-scale 2` +
    `yarn-orig-ctx 131072` to push past the 131072 native ceiling. The
    underlying `llama_context` *does* apply the scaling (KV cache grows to
    match, log shows `n_ctx_seq (262144) > n_ctx_train (131072)`), but
    `tools/server/server-context.cpp` unconditionally caps the usable slot
    context at `n_ctx_train` regardless (`"the slot context (262144) exceeds
    the training context of the model (131072) - capping"`,
    `server-context.cpp:1200-1204`) — there is no flag to override this. Net
    effect was strictly worse: +1.5GiB VRAM for a KV cache that's never
    actually used past 131072. Reverted. **Superseded 2026-08-17:** ctx
    went back to 262144 with `rope-scaling yarn` + `rope-scale 2` +
    `yarn-orig-ctx 131072` **plus**
    `override-kv = muse-glimmer.context_length=int:262144,dflash.context_length=int:262144` —
    rewriting the model's own `context_length` metadata to 262144 means
    `n_ctx_train` is 262144 and the cap never fires, so no `src/` patch is
    needed. It demonstrably works: the f16 KV pass benched real requests at
    192k depth on this preset (see the table above).
    Separately, `fit = on` interacts badly with `spec-draft-model` at an
    out-of-training ctx-size — its memory-fitting pass tries to measure the
    draft model at the oversized ctx and fails (`dflash requires ctx_other to
    be set`, `[spec] failed to measure draft model memory`), silently falling
    back to a smaller ctx rather than erroring loudly.
- **`fit` / `fit-target` removed from every preset** (was on the two Qwen3
  presets, Gemma, and briefly Muse-Glimmer) at Chris's request. All four
  affected presets were reloaded afterward and confirmed to still load
  cleanly at their explicit `ctx-size` with no regression.
- **`Muse-Glimmer-30B-UD-Q6_K_XL`** — **no longer present** (replaced by
  the `Muse-Glimmer-30B-UD-Q4_K_XL` preset above; model folder deleted;
  kept for its bench numbers). Was: Meta's agentic multimodal 30B, dense —
  not MoE; Gemma3-family sliding-window attention with `final_logit_softcapping`
  / `logit_scale`, embedded "Onyx ATEM" tool-calling chat template) — the
  architecture (`muse-glimmer`) wasn't in the vendored source when the model
  was downloaded; needed `scripts\update.ps1` to pull
  `ggml-org/llama.cpp#26841` before it would load at all (build 223 -> 247).
  Vision (`mmproj-Muse-Glimmer-30B-Q8_0.gguf`) was deliberately **not** wired
  in — VRAM is already tight without it (weights 26.3GB + DFlash drafter
  1.6GB = ~28GB resident, and the full 131072 native ctx at q8_0 KV lands at
  31.4GB/32.6GB used, ~1.2GB headroom). `ubatch-size`: bench-verified 1024
  over 512 (+5.9% pp, 3813->4038 t/s); 2048 added only +0.2% more, not worth
  it. Speculative decoding uses the model's own DFlash drafter
  (`dflash-kquant.gguf`, `spec-type = draft-dflash`, `spec-draft-n-max 4`,
  `spec-draft-p-min 0.6`) — confirmed active (`/slots` shows
  `speculative: true`) with ~80-90% draft acceptance, taking decode from the
  no-spec bench baseline of 57 t/s to ~67-73 t/s in real requests. The
  embedded chat template renders/parses cleanly as-is (no
  `chat-template-file` override needed) but the model always emits a
  substantial `reasoning_content` trace before the real answer regardless of
  prompt brevity — budget accordingly (a 128-token cap produced
  `finish_reason: "length"` with empty `content`; 512 was enough).
  `reasoning-preserve = on` set to match the Qwen3.6 presets' convention for
  heavy-reasoning models; verified it doesn't break multi-turn. `cache-reuse`
  confirmed working (a 2-turn follow-up reused 62 of 67 prior tokens) despite
  the upstream PR noting "state save/load disabled" for this arch — that
  caveat is about the separate `/slots` save-to-disk feature, not in-session
  cache-reuse. Tool-calling (`tools` + `tool_choice: auto`) smoke-tested
  clean and fast (no CPU-pegging symptom) but the ATEM format is new/lightly
  tested here — still a watch-item per the CPU-pegging note above, since this
  is explicitly an agentic, tool-heavy model.
- **2026-08-18: Gemma-4-31B-it-QAT-Abliterated replaced with
  `Gemma-4-12B-it-QAT-Abliterated`** (Chris wanted the underperforming 31B
  gone, replaced by a smaller uncensored Gemma-4 — chose 12B dense over a
  26B-A4B MoE alternative). Same publisher/pattern as before:
  `huihui-ai/Huihui-gemma-4-12B-it-qat-q4_0-unquantized-abliterated-GGUF`
  (Q4_K 7.38GB + `mmproj-model-bf16.gguf` 175MB + `mtp-ggml-model-bf16.gguf`
  862MB drafter — identical file layout to the old 31B slot, just smaller).
  **Configured without a live load test** (GPU was busy with other work at
  the time, per Chris's request) — everything below is analytical, not
  bench-verified, and should be treated as a first guess to revisit:
  - `ctx-size 262144` (full native `n_ctx_train`) at `f16`/`f16` KV. Derived
    by reading this gguf's own header metadata directly (range-fetched the
    first 20MB over HTTP, hand-parsed the GGUF KV section — `gguf-py`'s
    `GGUFReader` chokes on a truncated tensor-data section, so a minimal
    manual parser was used instead): 48 layers, 5-SWA-then-1-full repeating
    (40 SWA layers `kv_head=8`/`dim=256`, 8 full layers `kv_head=1`/
    `dim=512`), `sliding_window=1024`. Assuming llama.cpp's iSWA cache caps
    SWA-layer KV at the window size (validated by reproducing the existing
    31B's documented "~78 MiB/1k tokens" figure from its own metadata this
    same way — 60 layers, 10 full layers `kv_head=4`/`dim=512`, computed
    81,920 B/token vs. the documented 78 MiB/1k ~ 81,788 B/token, a match),
    12B's full-layer-only KV cost is ~16KiB/token, i.e. ~4.3GiB total KV at
    262144 vs. the 31B's ~20GiB — hence no need for the 31B's 73728 ctx cap.
    Total resident estimate (weights ~7.84GiB + KV ~4.3GiB + compute
    buffers) is ~14-16GiB, comfortably under the 5090's 32.6GiB even
    without knowing what else is loaded on the GPU concurrently.
  - `chat-template-file` kept pointed at the existing
    `Gemma31b_fixed_chat_template.jinja` rather than trying the embedded
    template — the file's own header says "Google Gemma 4 Canonical Chat
    Template" (family-wide, not 31B-specific), and the embedded template in
    this gguf opens with the same macro text, so it's presumably the same
    unfixed template the 31B needed this fix for. Unverified against a real
    request.
  - `ubatch-size 1024`, sampler (`temp 0.7`/`top-k 64`/`top-p 0.95`), and
    `spec-draft-n-max 4`/`p-min 0.5` carried over unchanged from the 31B's
    bench-verified values — not re-benched on this checkpoint.
  - **Untested**: chat template correctness, vision, tool-calling (this
    model is QAT + abliterated, the exact combo flagged in the
    CPU-pegging note above as highest-risk — this is now the top watch-item
    for that symptom), and whether 262144 actually fits alongside whatever
    else is on the GPU. Load-test and smoke-test before trusting this in
    agent mode.
- **`Ornith-1.5-35B-A3B-Q4_K_M`** (2026-08-19, `ornith-ai/Ornith-1.5-35B-A3B-GGUF`,
  `Ornith-1.5-35B-Q4_K_M.gguf` 21.7GB + `mmproj-Ornith-1.5-35B-BF16.gguf` 903MB) — new
  MoE 35B-A3B reasoning/coding slot, arch `qwen35moe` (41 blocks, hybrid SSM/attention,
  `full_attention_interval 4`, 256 experts/8 active, same family shape as
  `Qwen3.6-35B-A3B-MXFP4_MOE-BF16`). Base HF repo (`ornith-ai/Ornith-1.5-35B-A3B`) ships
  only safetensors; GGUF quants live in the sibling `-GGUF` repo.
  - **Load-tested for real** (`llama-bench`/`llama-server` probes, GPU free at the time):
    full native `n_ctx_train` **262144** loads fine text-only (31099/32607MiB, ~1.5GB
    headroom, no mmproj). With `mmproj` also loaded, Chris asked for ≥2.5GB headroom;
    `163840` gave 2.87GB but Chris chose to keep **`196608`** instead (measured
    30393/32607MiB used = **~2.16GB headroom** with mmproj loaded — a deliberate
    accepted tradeoff, not the ≥2.5GB target).
  - **MTP tensors present but not usable**: gguf metadata has
    `qwen35moe.nextn_predict_layers = 1` and `blk.40.nextn.{eh_proj,enorm,hnorm,
    shared_head_norm}.weight` tensors (confirmed via `uvx --from gguf gguf-dump`,
    `PYTHONIOENCODING=utf-8` needed on Windows or it crashes on BPE merge unicode).
    At load, `llama-server` logs every `blk.40.*` tensor including all four `nextn.*`
    ones as **"unused tensor ... ignoring"** — so unlike the 27B/35B MXFP4 slots,
    `spec-type = draft-mtp` would not actually get speculative decoding here. Left
    unset. Worth re-checking after a `scripts\update.ps1` pull in case upstream support
    for this arch's MTP layout lands later.
  - No `chat-template-file` — embedded `tokenizer.chat_template` verified directly via
    real requests: reasoning (`reasoning_content`/`content` split correctly), tool-calling
    (`tool_choice: auto` returned a correct call in ~1s, no CPU-pegging symptom — this is
    a stock, non-abliterated release), and vision (mmproj loaded, a real base64 image
    request correctly identified color) all smoke-tested clean.
  - Sampler (`temp 0.6`/`top-k 20`/`top-p 0.95`/`min-p 0.0`) and `reasoning-format
    deepseek` / `reasoning-preserve on` per the model card's `<think>`-tag convention,
    matching the repo's other Qwen3.5/3.6-family reasoning presets.
  - `toolCalling` enabled in `chatLanguageModels.json` given the clean tool-call smoke
    test above.
- **2026-08-19: Stock `Qwen3.8-27B-UD-Q4_K_M` added alongside the abliterated
  slot** (`unsloth/Qwen3.8-27B-GGUF`, `Qwen3.8-27B-UD-Q4_K_M.gguf` 16.46GB +
  `mmproj-BF16.gguf` 931MB) — brings back a stock (non-abliterated) 27B,
  which had previously existed as `Qwen3.8-27B-UD-Q4_K_XL` before that slot
  was removed and replaced by `Qwen3.8-27B-abliterated-Q4_K`; this is now a
  third preset family, not a replacement. Same `qwen35` arch, same
  `chat_template_kwargs.reasoning_effort` mechanism, confirmed via the HF
  API. Three presets (low/medium/xhigh), same naming convention as the
  abliterated block.
  - **MTP tensors confirmed present** via `gguf-dump` before enabling spec
    decode (not assumed): `qwen35.block_count = 65`,
    `qwen35.nextn_predict_layers = 1`, `blk.64.nextn.{eh_proj,enorm,hnorm,
    shared_head_norm}.weight` all present — same layout as the old
    Q4_K_XL slot. Configured `spec-type = draft-mtp`,
    `spec-draft-n-max 4` / `spec-draft-p-min 0.5` (carried over from the
    f16 KV pass values, not re-benched on this quant).
  - `ctx-size 163840` at `f16`/`f16` KV, with `mmproj-BF16.gguf` vision
    wired in — the same ctx value the old Q4_K_XL slot settled on with
    vision loaded (2.03GiB headroom there at 17.56GB+887MB weights); this
    Q4_K_M file is ~1.1GB smaller, so headroom should be equal or better.
    **Not load-tested this pass** (Chris explicitly opted out of a live
    load test) — VRAM fit, chat-template-file correctness, tool-calling,
    and vision are all unverified. Load-test and smoke-test before
    trusting this in agent mode, same caveat as other un-tested presets
    in this file.
  - `temp = 1.0` carried over from the old Q4_K_XL slot's documented
    exception (Qwen's own published thinking-mode value for this base
    model) — `top-k 20`/`top-p 0.95`/`min-p 0.0` otherwise match the
    repo's Qwen convention.
  - `chat-template-file` set to `Qwen-Sharp-Chat-Template.jinja` (the old
    Q4_K_XL slot predated that template and used the embedded one; this
    new slot uses it from the start for consistency with the abliterated
    27B and the 35B MoE presets).
  - `batch-size`/`ubatch-size 2048`, matching the old Q4_K_XL slot's final
    tuned values (not re-benched here).
- **2026-08-20: `Qwen3.8-27B-abliterated-Q4_K` removed** (`models.ini`, all
  three low/medium/xhigh presets, and the gguf on disk deleted) — superseded
  by the stock `Qwen3.8-27B-UD-Q4_K_M` family added the day before; Chris
  opted to drop the abliterated 27B slot rather than keep both.
- **2026-08-21: `Qwen3.8-27B-NVFP4-N4_0` added as a fourth Qwen3.8-27B
  preset family** (`akopytko/Qwen3.8-27B-NVFP4-GGUF`,
  `Qwen3.8-27B-NVFP4-MTP-N4_0.gguf` 15.7GB + `mmproj-BF16.gguf` 931MB) —
  a Blackwell-native NVFP4 quant (RTX 50-series/DGX Spark/B200/B300 only;
  per the model card, omits global per-tensor scales and uses quantize-time
  MSE scale optimization vs. standard NVFP4, ~50% faster than conventional
  4-bit quants at similar VRAM). Deliberately did **not** pull the sibling
  `Qwen3.8-27B-NVFP4-MTP-Q6_K.gguf` in the same repo — N4_0 only, per Chris.
  Same `qwen35` arch as the other three Qwen3.8-27B families, so mirrored
  the stock `Qwen3.8-27B-UD-Q4_K_M` preset verbatim (three low/medium/xhigh
  presets, same `ctx-size 163840`, sampler, `chat-template-file
  Qwen-Sharp-Chat-Template.jinja`, `reasoning-format deepseek`) rather than
  re-deriving values, since this pass has no load test.
  - **MTP tensors confirmed present** via `gguf-dump` before enabling
    `spec-type = draft-mtp` (embedded head, matching the model card's "MTP
    Integration" claim) — no `spec-draft-model` sidecar needed, same as the
    UD-Q4_K_M family. `spec-draft-n-max 4` / `spec-draft-p-min 0.5` carried
    over unchanged.
  - **Load-tested 2026-08-21** (initial pass skipped it, Chris asked for a
    full router pass after): router restarted to pick up the new presets
    (hot server doesn't reread `models.ini`), then `Qwen3.8-27B-NVFP4-N4_0`
    (xhigh) loaded through the router at the full `ctx-size 163840` with
    vision — **31.4/32.6 GB VRAM used, ~1.2GB headroom, no OOM.** That's
    tighter than the UD-Q4_K_M sibling's measured headroom at the same ctx,
    consistent with NVFP4 weights being similar size but this being a fresh
    quant format, not re-benched for max safe ctx on this specific file —
    if VRAM gets tight from other GPU load, drop ctx before assuming a
    config bug.
    Smoke-tested via the OpenAI-compatible endpoint: plain chat (`finish_reason
    stop`, correct answer, thinking properly separated into
    `reasoning_content` — chat-template-file + reasoning-format both
    correct), tool-calling (`finish_reason tool_calls`, correct function
    name/args), and vision (accurate image description via a data-URI
    image). MTP confirmed live in the `timings` field (`draft_n`/
    `draft_n_accepted` around 40-70% acceptance across the three calls),
    not just present in the gguf metadata. `llama-bench` baseline (no
    spec-decode, no vision, `-p 1024 -n 256`): pp1024 7619 t/s, tg256
    83.3 t/s on 14.63GiB weights.
  - **Live-server prefill/gen perf at small/medium/large depth** (2026-08-21,
    real HTTP requests against the loaded router, MTP + vision both active,
    `cache_prompt: false` to force genuine cold prefill — an initial pass
    was thrown out because `cache-reuse` matched a shared prefix across
    requests that reused the same filler text, inflating the deep-context
    numbers):

    | depth | prompt tokens | prefill (pp) | gen (tg) | draft accept |
    |---|---:|---:|---:|---:|
    | small  | 2,178   | 2555 t/s | 88.5 t/s | 27/41 (66%) |
    | medium | 80,179  | 3221 t/s | 81.6 t/s | 32/38 (84%) |
    | large  | 133,579 | 2035 t/s | 84.6 t/s | 43/46 (93%) |

    Prefill peaks mid-depth and drops off at large depth, consistent with
    the KV-cache-cost-at-depth pattern already documented above for
    `Q4_K_XL`. Generation speed stays flat (81-89 t/s) across depth and
    lands close to the no-MTP `llama-bench` baseline (83.3 t/s) despite
    MTP being active — draft acceptance was decent (66-93%, rising with
    depth) but didn't net a large speedup here, worth knowing before
    assuming MTP is buying much at this ctx/VRAM pressure. "Large" landed
    at 133.6k tokens (~82% of the `163840` ctx ceiling) rather than the
    145k originally targeted — the filler-text tokenizer ratio came in
    higher than assumed when sizing the synthetic prompt.
- **2026-08-18: Muse-Glimmer's stock slot swapped back to an abliterated
  release**, at Chris's explicit request (after initially just asking to
  add "-Abliterated" to the *existing* stock model's name — flagged that
  the current slot was genuinely non-abliterated stock, so renaming alone
  would mislabel it; Chris chose to actually swap in a real abliterated
  model instead). New preset:
  **`Muse-Glimmer-30B-Abliterated-Q4_K_M`**
  (`Blackfrost-AI/Muse-Glimmer-30B-Abliterated-GGUF` — refusal removed via
  a "Blackfrost weight-change process", card claims 0/450 measured true
  refusals on their R1-HARMFUL-BENCH-450 suite). Picked the publisher's own
  matched "compact" trio rather than mixing precisions: `Q4_K_M` main
  (15.8GB), `mmproj-...-Q4_K_M.gguf` (1.40GB, down from the old stock
  slot's Q8_0 1.91GB), `dflash-...-Q4_K_M.gguf` (1.63GB) — the model card's
  2026-08-15 "Improvement update" explicitly ships these three as a
  matched consumer-hardware set. Total weights ~17.5GiB, actually *smaller*
  than the old stock preset's ~18.2GiB.
  **Configured without a live load test** (GPU busy with other work, per
  Chris's request) — same `ctx-size 262144` via the same YaRN trick already
  proven on this exact architecture (`rope-scaling yarn`, `rope-scale 2`,
  `yarn-orig-ctx 131072`, `override-kv =
  muse-glimmer.context_length=int:262144,dflash.context_length=int:262144`)
  — confirmed via metadata that this release uses the identical
  `muse-glimmer.context_length` / `dflash.context_length` keys and native
  131072 ctx as the old stock model, so the same trick should carry over,
  but this specific checkpoint was not itself load-tested at 262144.
  - `chat-template-file` set to a new
    `Muse-Glimmer-Abliterated-Chat-Template.jinja` (copied verbatim from
    the repo's own `chat_template.jinja`) rather than relying on the
    embedded template — the model card says this fixed template
    "normalizes `Reasoning effort` to `Reasoning strength`" (a real bug fix
    vs. the stock template). **Chris was told and confirmed keeping it
    as-is**: this template also auto-injects an aggressive "AI assistant...
    Prime Directive — Operator Sovereignty... Refusal is not in your
    vocabulary" system prompt whenever a request carries no explicit system
    message (checked the template source directly —
    `{%- if not ns.has_system -%}` branch, ~line 148). This will change
    default behavior for any client (VS Code chat, opencode, pi) that
    doesn't always send its own system message.
  - Sampler set to the model card's own "Confirmed settings" rather than
    the repo's old deliberately-conservative Muse-Glimmer values: `temp
    1.0`/`top-k 64`/`top-p 0.95` (vs. the old stock slot's `0.8`/`40`/
    `0.95`).
  - `spec-draft-n-max` raised to **15** (up from the old slot's
    bench-verified 4) per the card: confirmed via this release's own
    `dflash-*.gguf` metadata (`dflash.block_size = 16`) that 15 is the
    trained block size minus one, i.e. the architectural max, not just a
    generic suggestion — `spec-draft-p-min` left at the repo's existing
    0.6 (the card's CLI example doesn't override it, and
    `common/speculative.cpp`'s dflash impl does honor `p_min` for
    confidence gating).
  - **Untested**: whether 262144 actually fits (weights are smaller than
    before but this is a different quant mix, not a verified-equivalent
    swap), chat template correctness/tool-call parsing, vision, and the
    CPU-pegging symptom (this is an abliterated fine-tune, one of the two
    flagged risk categories). Load-test and smoke-test before trusting
    this in agent mode — this note previously flagged the prior abliterated
    Muse-Glimmer slot as the single highest-priority CPU-pegging watch-item
    before it was removed; the same applies here.
- **2026-08-22: `Gemma-4-26B-A4B-it-QAT` added** (`unsloth/gemma-4-26B-A4B-it-qat-GGUF`,
  `gemma-4-26B-A4B-it-qat-UD-Q4_K_XL.gguf` 14.25GB + `mmproj-BF16.gguf` 1.19GB +
  `MTP/mtp-gemma-4-26B-A4B-it-Q8_0.gguf` drafter 462MB) — new MoE Gemma-4 slot,
  `gemma4` arch (128 experts/8 active + 1 shared, 30 text layers, native ctx
  262144), stock instruct QAT checkpoint (not abliterated, unlike the existing
  12B slot). This exact model/repo had been referenced once before in this file
  as a candidate but was never actually added until now.
  - **Found and fixed a real vision bug shared with `Gemma-4-12B-it-QAT-Abliterated`**:
    `templates\Gemma31b_fixed_chat_template.jinja` silently dropped all image
    input. `tools/server/server-common.cpp` (`oaicompat_chat_params_parse`)
    rewrites every `image_url` content part into `{"type": "media_marker",
    "text": get_media_marker()}` *before* the Jinja template ever sees it — the
    template is expected to just emit `item.text` verbatim so the server can
    later find that marker substring in the rendered prompt and splice in the
    real image embedding via mtmd. This template's image-handling branches only
    matched `type in ['image', 'image_url']`, which never matches post-rewrite,
    so images were dropped with **no error and no log line** (the base64-data-URI
    path in `handle_media()` logs nothing either way) — the request just silently
    proceeded text-only, burning the full image token budget on nothing. Checked
    for a pre-fixed public template before patching: neither Google's current
    official `gemma-4-31B-it` `chat_template.jinja` nor a community
    llama.cpp-oriented fork (`asf0/gemma4_jinja`) handle `media_marker` either —
    this is a generic gap in every Gemma-4 template found, not specific to this
    fork. Fixed locally by adding an `elif part.get('type') == 'media_marker'`
    (tool-response block) and `elif item.get('type') == 'media_marker'` (main
    content block) case that emits `part.get('text')`/`item.get('text')`,
    mirroring how `Qwen-Sharp-Chat-Template.jinja` handles it (there, by
    accident, via a generic `elif 'text' in item` fallback it already had).
    Verified fixed with a real base64 PNG request (correct "Red" answer,
    `prompt_tokens` jumped from 29 to 78 once the image was actually embedded).
    **This fix is retroactive** — since both Gemma-4 slots share this template
    file, `Gemma-4-12B-it-QAT-Abliterated`'s vision (previously flagged
    "untested" in its own note above) also benefits. Independently re-verified
    on the 12B slot itself (2026-08-22): a real base64 image request correctly
    identified "Blue," `prompt_tokens` 78 (real vision tokens embedded,
    `finish_reason: stop`) — confirmed fixed, not just theorized.
  - **Context**: `probe-ctx-headroom.ps1` live-tested full native `262144` —
    fits with `26535/32607MiB` used, **~6.1GiB headroom**, no reduction needed.
    Notably roomy for this repo (most presets target ~2GiB) simply because
    total weights (main+mmproj+drafter) are only ~14.9GiB, small for a
    262144-ctx slot.
  - `chat-template-file` reused the existing `Gemma31b_fixed_chat_template.jinja`
    (its header says "Google Gemma 4 Canonical Chat Template", family-wide, not
    31B-specific) rather than adding a new file — same template family
    confirmed via this repo's card (standard roles + `<|think|>`-token thinking
    mode).
  - Sampler (`temp 0.7`/`top-k 64`/`top-p 0.95`/`min-p 0.0`) and
    `spec-draft-n-max 4`/`p-min 0.5` carried over unchanged from the 12B
    sibling's bench-verified values, not re-benched on this MoE checkpoint.
    No `reasoning = on` set, matching the 12B sibling's precedent, despite the
    model having a `<|think|>`-token thinking mode — `reasoning_content`
    populates anyway via the template's own thinking-tag handling even without
    the flag (confirmed live), so this may be worth revisiting.
  - **Smoke-tested clean**: plain chat (`finish_reason stop`, correct
    reasoning/content split), tool-calling (`tools`+`tool_choice: auto`,
    correct function/args, ~0.6s, no CPU-pegging symptom despite this being a
    QAT release — one of the two flagged risk categories), vision (post-fix,
    see above), MTP active (`draft_n`/`draft_n_accepted` confirmed in
    `timings`, ~40-78% acceptance across test requests). `toolCalling` enabled
    in `chatLanguageModels.json`.
  - **Overthinking on self-referential counting prompts, fixed with
    `reasoning-budget`**: on "say hello in exactly five words," the model
    looped in `reasoning_content` re-counting candidate phrases and hit
    `finish_reason: length` at both 100 and 600 `max_tokens` without ever
    emitting a visible answer. Not the CPU-pegging symptom (fast, bounded,
    just token-budget-hungry on this one prompt class) — normal factual/tool
    prompts terminate correctly and quickly. Fixed by adding
    `reasoning-budget = 8192` to this preset (2026-08-22), matching this
    repo's old pre-removal convention for the closest comparable model
    (`Qwen3.8-27B` used `8192`, the 35B MoE used `4096` — see the
    `reasoning-budget` removal note above for the mechanism: it forces an
    end-of-thinking tag once the token count is hit, coming out of the same
    shared budget as everything else, not a separate pool). Re-tested at
    `max_tokens 8500`: converged on its own at 1627 tokens
    (`finish_reason: stop`, correct 5-word answer), well under the cap — the
    budget is a backstop for worse cases, not something that fired on this
    particular retest. This is currently the only preset in the repo with
    `reasoning-budget` set again since the repo-wide removal.
