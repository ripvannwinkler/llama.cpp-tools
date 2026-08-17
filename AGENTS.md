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

## Downloading models

Use `hf download <repo> <file> --local-dir D:\llama.cpp\models\<model-id>` rather
than raw `curl` — `HF_HOME` is already set to `d:\.huggingface`, so it picks up
the cache and the configured token (faster/authenticated where that matters,
e.g. gated repos) automatically with no extra flags. Verify the finished file's
size against the repo's authoritative `Content-Length` (`curl -sIL <url>`)
regardless of which method downloaded it — don't trust a clean exit code alone,
a dropped connection can leave a silently truncated file.

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

- `[*]` global defaults: `n-gpu-layers = 99`, `flash-attn = on`,
  `ctx-size = 8192` fallback.
- Every per-model preset should set an explicit `ubatch-size` — a preset
  missing this falls back to llama.cpp's default (512), which at very large
  `ctx-size` can turn model load into a long CPU-bound stall (high CPU,
  server never becomes healthy/ready) rather than a fast GPU-bound one.
  Bench-verified values: `2048` for the MoE presets (+8.6% prompt
  processing vs 1024 on the 35B NVFP4), `1024` for the dense 27B (2048
  gained only 1.3% there).
- **Sampling params** — every preset now states its full sampler set explicitly
  rather than inheriting llama.cpp's defaults (`src/common/common.h`: `temp 0.8`,
  `top_k 40`, `top_p 0.95`, `min_p 0.05`), which match no model card used here.
  Convention: Qwen3.6 / Ornith (Qwen3.5-MoE-based) get `temp 0.6` / `top-k 20` /
  `top-p 0.95` / `min-p 0.0` — Qwen's published thinking-mode values. Gemma-4
  gets `top-k 64` and `temp 0.7` (the card says `1.0`; `0.7` is a deliberate
  middle ground for agentic coding). Muse-Glimmer is the least grounded — `0.6` /
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
  71 -> 146 t/s greedy coding on the older NVFP4 quant). The 35B NVFP4
  and Ornith exit on load if `spec-type` is set (MTP was stripped in those
  quants). The unsloth Gemma-4-31B-it GGUF ships a separate
  `mtp-gemma-4-31B-it.gguf` drafter (Q8_0) that works with any quant of
  the same model — configure via `spec-draft-model` + `spec-type = draft-mtp`
  + `spec-draft-n-max 4` (70 -> 101 tok/s on Q4_K_M).
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
    less reliably than a stock instruct release (e.g.
    `Qwen3.6-35B-A3B-NVFP4`, unaffected). The original unsloth 31B QAT was
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
    ctx-size 163840, cache-reuse 256, same custom chat template) — note it
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
    tuning above — already strong on the new quant, not re-swept.
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
  - Preset explicitly sets `xhigh` (matching the template's own default).
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
    actually used past 131072. Reverted. Don't retry this without patching
    upstream `src/` (which `scripts/update.ps1` would overwrite anyway).
    Separately, `fit = on` interacts badly with `spec-draft-model` at an
    out-of-training ctx-size — its memory-fitting pass tries to measure the
    draft model at the oversized ctx and fails (`dflash requires ctx_other to
    be set`, `[spec] failed to measure draft model memory`), silently falling
    back to a smaller ctx rather than erroring loudly.
- **`fit` / `fit-target` removed from every preset** (was on the two Qwen3
  presets, Gemma, and briefly Muse-Glimmer) at Chris's request. All four
  affected presets were reloaded afterward and confirmed to still load
  cleanly at their explicit `ctx-size` with no regression.
- **`Muse-Glimmer-30B-UD-Q6_K_XL`** (Meta's agentic multimodal 30B, dense —
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
