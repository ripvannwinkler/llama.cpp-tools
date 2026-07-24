# Local llama.cpp inference (RTX 5090)

Self-hosted, CUDA-built `llama-server` replacing LM Studio. OpenAI-compatible API with
LM Studio-style on-demand model switching (router mode). Everything lives under `D:\llama.cpp\`.

## TL;DR — daily use

```powershell
D:\llama.cpp\start-llama.ps1     # start (auto-runs at login too); prints model list
D:\llama.cpp\stop-llama.ps1      # stop cleanly (frees all VRAM)
D:\llama.cpp\restart-llama.ps1   # stop then start (picks up models.ini changes)
D:\llama.cpp\unload-llama.ps1    # free VRAM but keep the server running (for another GPU task)
D:\llama.cpp\load.ps1 <name>     # switch the loaded model (fuzzy match); starts server if down
```

- API base URL: **`http://127.0.0.1:8080/v1`** (any non-empty API key)
- Built-in web UI: open **`http://127.0.0.1:8080`** in a browser (has a model dropdown)
- Switch models by setting the request's `model` field to an id from `GET /v1/models`, or use `load.ps1`

## Layout

```
D:\llama.cpp\
  src\            cloned ggml-org/llama.cpp + build\bin\ (llama-server.exe, ...)
  models\         GGUFs, one folder per model:  <publisher>__<repo>\*.gguf
  models.ini      per-model config (context, batch/ubatch, KV quant, lite preset, ...)
  start-llama.ps1 launch router server (hidden), waits for health
  stop-llama.ps1      tree-kill the server (avoids orphaned VRAM)
  restart-llama.ps1   stop then start (for config/model.ini reloads)
  unload-llama.ps1    unload all models to free VRAM, server stays up
  load.ps1        switch the loaded model (fuzzy match / menu), starts server if needed
  bench.ps1        pick a model and benchmark it (llama-bench)
  probe-ctx.ps1    find each model's max context via llama-bench (used to build models.ini)
  update.ps1       git pull + recompile the CUDA build, then restart
  README.md       this file
  server.out.log / server.err.log   runtime logs
```

## Hardware / toolchain (as built)

- GPU: **NVIDIA RTX 5090, 32 GB** (Blackwell, compute capability **sm_120**).
- Preinstalled, no downloads needed: CUDA Toolkit **13.3**, MSVC (VS 18 Community), CMake + Ninja (bundled in VS), git.
- Measured: 27B Q4_K_M ≈ **72 tokens/sec** eval, fully GPU-offloaded.

## Updating

```powershell
D:\llama.cpp\update.ps1              # stop server, git pull, rebuild, restart
D:\llama.cpp\update.ps1 -NoRestart   # update + rebuild but leave the server stopped
```

`update.ps1` stops the server first (a running `llama-server.exe` locks the binary so the link step
would fail), pulls latest source, does an **incremental** CUDA rebuild in the VS dev environment,
prints old→new version, and restarts the server if it was running.

## Building / rebuilding (manual)

`update.ps1` does this for you; the manual steps are here for reference. Run from an
**"x64 Native Tools Command Prompt for VS 18"** (or a shell that has run `vcvars64.bat`), so
`cl.exe` + CUDA are on PATH.

```sh
cd D:\llama.cpp\src
git pull
cmake -B build -G Ninja -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_CUDA_ARCHITECTURES=120
cmake --build build --config Release -j
```

- `-DGGML_CUDA=ON` is the key flag; `120` = sm_120 (RTX 5090).
- Confirm it's a CUDA build: `build\bin\llama-server.exe` startup log shows `CUDA0: NVIDIA GeForce RTX 5090`,
  and `build\bin\ggml-cuda.dll` exists. `nvidia-smi` lists `llama-server.exe` as a GPU compute process when a model is loaded.

## Models

Copied from LM Studio (`C:\Users\Chris\.lmstudio\models`, originals kept). Current ids and the
verified per-model config (max context that loads on the 32 GB card — see `models.ini`):

| Model id (use in API `model` field) | Type | ctx | KV | ubatch |
|---|---|---|---|---|
| `knoopx__Qwen3.6-35B-A3B-NVFP4-GGUF` | 35B MoE, NVFP4 | 262144 | q8_0 | 1024 |
| `Richarlie__Qwen3.6-27B-Fable-Fusion-711-...-MTP-GGUF` | 27B dense, reasoning, Q4_K_M | 262144 | q8_0 | 1024 |
| `lmstudio-community__gemma-4-31B-it-QAT-GGUF` | 31B, reasoning, QAT Q4_0 | 262144 | **q5_1** | 512 |
| `lmstudio-community__Qwen3-VL-8B-Instruct-GGUF` | 8B vision, Q8_0 | 262144 | q8_0 | 1024 |
| `mradermacher__Qwen2.5-VL-7B-NSFW-Caption-V3-abliterated-GGUF` | 7B vision, Q8_0 | 128000 | q8_0 | 1024 |
| `qwen3-vl-8b-lite` | 8B vision (same file), low-footprint | 8192 | q8_0 | 1024 |

Notes: gemma uses **q5_1** KV (one scale below q8) so its full 262144 context fits with headroom;
q8 only reached 196608. The 7B caps at its trained max of 128000. gemma keeps `ubatch = 512` (tight VRAM);
raising it would OOM. `qwen3-vl-8b-lite` is a second preset pointing at the 8B's file (see below).

### Adding a model

Router discovery is **one level deep only**: `models\<name>\*.gguf`, pairing the model gguf with an
`mmproj*.gguf` in the same folder. LM Studio's two-level `<publisher>\<repo>\` layout yields **0 models** —
that's why everything was flattened to `<publisher>__<repo>\`. Drop a new model in its own folder under
`models\`, then restart the server. The folder name becomes the model id.

## Per-model configuration — `models.ini`

Referenced by `start-llama.ps1` via `--models-preset`. INI with a `[*]` global section plus one
`[<model-id>]` section each. Keys are any `llama-server` flag **without dashes** (long form, short form,
or `LLAMA_ARG_*` env name), e.g. `ctx-size` / `c`, `batch-size` / `b`, `cache-type-k` / `ctk`.

```ini
version = 1

[*]                        ; defaults for every model
n-gpu-layers = 99
flash-attn   = on          ; required for quantized V cache
ctx-size     = 8192

[lmstudio-community__Qwen3-VL-8B-Instruct-GGUF]
ctx-size     = 65536

[Richarlie__Qwen3.6-27B-Fable-Fusion-711-Uncensored-Heretic-NM-DAU-NEO-MAX-MTP-GGUF]
ctx-size     = 24576
cache-type-k = q8_0
cache-type-v = q8_0
```

- `ctx-size = 0` means "use the model's trained max" — **not** "fit to VRAM". There is no auto-fit-to-VRAM
  option; you set an explicit number (or `0` for the model max) and make it fit via context and/or KV quant.
- KV quant values (smaller = more headroom, slight quality loss): `f16` (default), `bf16`, `q8_0`
  (near-lossless, ~2x), `q5_1`, `q5_0`, `q4_1`, `q4_0`, `iq4_nl`. Quantized V cache requires `flash-attn = on`.
- **`batch-size` vs `ubatch-size`** (both affect prompt processing / prefill, not generation):
  `batch-size` (`-b`, default 2048) = logical batch (max tokens gathered per round); `ubatch-size`
  (`-ub`, default 512) = physical micro-batch actually run per GPU forward pass. Bigger `ubatch` → faster
  prefill but larger compute buffer (more VRAM); it does NOT change KV size. Must have `batch ≥ ubatch`.
- Preset-only keys: `load-on-startup` (bool), `stop-timeout` (seconds).
- **Precedence (highest wins): CLI args > `[model-id]` > `[*]`.** Because a CLI arg overrides every preset
  value, `-ngl`/`-fa` are deliberately kept in `[*]` (not on the start-script CLI) so they stay per-model overridable.
- A running model must be **reloaded** (stop/start the server) to pick up INI edits.
- Verify an override applied: load the model, then `GET /props?model=<url-encoded id>` → `default_generation_settings.n_ctx`.

### Multiple presets for one model (e.g. a "lite" low-context variant)

A section whose name does **not** match an auto-discovered model becomes a **new model id**, as long as it
specifies a `model` path (and `mmproj` for vision). Point it at the same GGUF with a smaller context to get
a low-VRAM variant you can run alongside other GPU work — this is what `qwen3-vl-8b-lite` does:

```ini
[qwen3-vl-8b-lite]
model  = D:\llama.cpp\models\lmstudio-community__Qwen3-VL-8B-Instruct-GGUF\Qwen3-VL-8B-Instruct-Q8_0.gguf
mmproj = D:\llama.cpp\models\lmstudio-community__Qwen3-VL-8B-Instruct-GGUF\mmproj-Qwen3-VL-8B-Instruct-F16.gguf
ctx-size = 8192
```

Both ids appear in `/v1/models`; no duplicated files on disk. Custom-section paths should be **absolute**
(relative paths resolve against the server's CWD, which is `C:\Windows\System32` under the login task).

### Finding a model's max context

`probe-ctx.ps1` binary-searches each model's largest context that loads (via `llama-bench`, which self-exits
per probe). Its numbers are single-sequence and slightly optimistic vs the router, so always confirm by
actually loading through the router — the values in `models.ini` are router-verified.

## Switching the loaded model

```powershell
D:\llama.cpp\load.ps1            # numbered menu of all models
D:\llama.cpp\load.ps1 gemma      # fuzzy match (careful: "lite" also matches "ab-lite-rated")
D:\llama.cpp\load.ps1 8b-lite    # loads qwen3-vl-8b-lite
```

Starts the server if it isn't running, then POSTs `/models/load` and **waits** for the load to finish
(the endpoint is async) before reporting VRAM. Alternatives: the **web UI dropdown** at
`http://127.0.0.1:8080`, selecting the model in your client (opencode), or just sending a chat request with
a new `model` value (lazy auto-load). Under `--models-max 1` any of these evicts the current model.

## Freeing VRAM for another GPU task

To reclaim VRAM without stopping the server:

```powershell
D:\llama.cpp\unload-llama.ps1     # unloads all loaded models, server stays up
```

`/models/unload` unloads **one named model per call** (there's no single "unload all" endpoint), so the
script iterates every model id and unloads whatever is running. Manual equivalent:

```sh
curl -X POST http://127.0.0.1:8080/models/unload -H "Content-Type: application/json" -d '{"model":"<id>"}'
```

**Caveat:** `--models-autoload` is on, so the **next API request reloads a model** and re-takes VRAM. While
your other GPU task runs, don't send requests to `:8080`. For a hard guarantee that nothing reloads, use
`stop-llama.ps1` instead and `start-llama.ps1` afterwards.

## Benchmarking

`bench.ps1` wraps `llama-bench` (the CLI benchmark tool): it lists your models, lets you pick one,
applies that model's `cache-type-k/v` from `models.ini`, frees VRAM from the running server first
(so it won't OOM), then reports prompt-processing (`pp`) and token-generation (`tg`) speed in t/s.

```powershell
D:\llama.cpp\bench.ps1                                   # interactive menu (512 prompt / 128 gen)
D:\llama.cpp\bench.ps1 -Model lmstudio-community__Qwen3-VL-8B-Instruct-GGUF
D:\llama.cpp\bench.ps1 -PromptTokens 2048 -GenTokens 256 -Depth 4096 -Reps 5
```

Params: `-Model <id>` (skip the menu), `-PromptTokens` (`-p`), `-GenTokens` (`-n`), `-Depth` (`-d`,
speed at a given context depth), `-Reps` (`-r`), `-NGL`, `-FlashAttn on|off|auto`, `-NoUnload`.

`llama-bench` loads its **own** copy of the model (it does not use the router), which is why the script
frees the server's VRAM first. Reading `pp` = prefill/prompt throughput, `tg` = generation throughput.

## Auto-start at login

Registered as a per-user Task Scheduler task **`llama-server`** (At-logon trigger) that runs
`start-llama.ps1` hidden — same behavior LM Studio had. It only *starts* the server (no watchdog), so
`stop-llama.ps1` stays in effect until the next login.

```powershell
Start-ScheduledTask      -TaskName llama-server
Disable-ScheduledTask    -TaskName llama-server   # stop auto-starting at login
Enable-ScheduledTask     -TaskName llama-server
Unregister-ScheduledTask -TaskName llama-server -Confirm:$false
```

## opencode integration

`~/.config/opencode/opencode.json` → provider `llama-local` points at `http://127.0.0.1:8080/v1`.
Model **keys must exactly match the router ids** (the flat folder names above). opencode only lists the
models defined there (not raw `/v1/models`).

## Gotchas / lessons learned

- **Stopping: never kill the port PID alone.** It orphans the model child process, which keeps holding
  VRAM (saw ~30 GB stuck). Always use `stop-llama.ps1` (does `taskkill /PID <pid> /T /F`), or
  `POST /models/unload` to free VRAM without stopping the server.
- **Reasoning models return empty `content` if `max_tokens` is too small.** `gemma-4`, `Qwen3.6-*` are
  reasoning models: thinking goes to `message.reasoning_content`, the answer to `message.content`. With too
  few tokens the response hits the length limit while still thinking → empty `content`, `finish_reason=length`.
  Fix: give generous `max_tokens`, read `reasoning_content` separately, or disable thinking per model.
- **Raw `/v1/models` lists a harmless `default` pseudo-model** (artifact of the `version = 1` header). Ignore it.
- **`--models-max 1`** is set on purpose: only one model in VRAM at a time (32 GB is tight for 27–35B).
- LM Studio still has its own copies at `C:\Users\Chris\.lmstudio\models`; relaunch it any time as a fallback.

## Useful endpoints

- `GET  /health` — readiness
- `GET  /v1/models` — discovered model ids
- `POST /v1/chat/completions` — chat (set `model`)
- `GET  /props?model=<id>` — effective settings for a loaded model (n_ctx, etc.)
- `POST /models/load` / `POST /models/unload` — manual load/unload
- Web UI at `/`

## References

- Model management / router mode: https://huggingface.co/blog/ggml-org/model-management-in-llamacpp
- Server docs (presets, routing): `src\tools\server\README.md`
- Build docs: https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md
