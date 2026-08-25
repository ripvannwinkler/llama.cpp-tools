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

## Speculative decoding — `ngram-mod` beats embedded MTP here

Measured 2026-08-25 on the RTX 5090 (32 GiB), `--parallel 3`, temp 0, medians of
3 reps over three workloads: **copy** (echo a ~30-line class back with one
rename), **agentic** (echo it back with two methods added), **novel** (400 words
of new prose). Values are tok/s.

| model | spec-type | copy | agentic | novel |
|---|---|---:|---:|---:|
| KAT-Coder-V2.5-Dev (35B-A3B MoE) | `none` | 236.7 | 238.2 | 236.6 |
| | `draft-mtp` | 182.0 | 173.8 | 124.9 |
| | `ngram-mod` | **470.1** | **438.6** | **240.6** |
| Qwen3.6-35B-A3B (MoE) | `none` | 194.2 | 194.8 | 195.1 |
| | `draft-mtp` | 203.1 | 187.5 | 127.7 |
| | `ngram-mod` | **235.4** | **232.6** | **196.3** |
| Qwen3.8-27B (dense) | `none` | 75.9 | 75.9 | 76.0 |
| | `draft-mtp` | 112.8 | 90.6 | 71.0 |
| | `ngram-mod` | **183.7** | **108.2** | **76.3** |

- **`draft-mtp` is a net loss on the A3B MoEs** — up to 47% slower than no
  speculation at all, *despite* 92–95% draft acceptance. High acceptance is not
  evidence the head pays for itself: with only ~3B active params a decode step is
  so cheap that the grafted MTP head's fixed cost dominates. Don't tune
  `spec-draft-p-min`/`n-max` chasing acceptance — measure tok/s against `none`.
- On the **dense** 27B the economics flip and `draft-mtp` does win standalone
  (+49% copy), but `ngram-mod` alone still beats it on every workload, and
  `draft-mtp,ngram-mod` is worse than `ngram-mod` alone everywhere.
- `ngram-mod` is never worse than no speculation (it ties baseline on novel
  prose) and roughly doubles copy-heavy/editing throughput. It's the default for
  any preset with an **embedded** MTP head:

  ```ini
  spec-type              = ngram-mod
  spec-ngram-mod-n-min   = 8
  spec-ngram-mod-n-max   = 24
  spec-ngram-mod-n-match = 48
  ```

- Gains scale with how much of the output already appears in the input, so a
  reasoning-heavy preset (e.g. the 27B at `reasoning_effort: xhigh`) lands nearer
  the agentic column than the copy column.

Not covered by these runs: both Gemma presets and Muse-Glimmer use an **external**
drafter (`spec-draft-model`, `spec-type = draft-mtp`/`draft-dflash`) — a different
mechanism that was never measured, so leave them alone unless you benchmark them.
`Ornith-1.5-35B-A3B` was switched to `ngram-mod` by inference from the other two
A3B MoEs, not measured directly.

Note `llama-bench` (and so `scripts\bench.ps1`) ignores all `spec-*` settings —
speculative decoding can only be benchmarked through the server, using the
`timings` object returned on each `/v1/chat/completions` response
(`predicted_per_second`, `draft_n`, `draft_n_accepted`).

## Context sizing — verify the probe under load

`scripts\probe-ctx-headroom.ps1` launches its own server with `--parallel 2` and
samples VRAM on an **idle** model, but `start-llama.ps1` runs the router with
`--parallel 3` and compute buffers grow during generation. The probe therefore
over-reports free VRAM by roughly 380 MiB. Pad the target (`-HeadroomMiB 2450` to
land near 2048 in practice) and re-check `nvidia-smi` during a real generation
before treating a context as final.

Re-probe after any change that frees VRAM: dropping `draft-mtp` unloads the MTP
head and returned ~1.5 GiB on KAT-Coder, taking it from `131072` to `212992`.

`Qwen3.8-27B-NVFP4-MTP-VERY-HIGH` is a deliberate exception — leave its
`ctx-size` at `131072` even though more would fit.

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
  `65536` everywhere) — leave it alone unless asked.
- `C:\Users\Chris\.pi\agent\models.json` — pi's `llama-local` provider.
  Each entry under `providers.llama-local.models[]` keys by `id` (the
  `models.ini` section name); `contextWindow` = that section's `ctx-size`.
  `maxTokens` is likewise a deliberate choice (`65536` everywhere), not
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

