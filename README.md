# Local llama.cpp inference (RTX 5090)

Self-hosted, CUDA-built `llama-server` replacing LM Studio. OpenAI-compatible API with
LM Studio-style on-demand model switching (router mode). Everything lives under `D:\llama.cpp\`.

## TL;DR — daily use

```powershell
D:\llama.cpp\scripts\start-llama.ps1     # start (auto-runs at login too); prints model list
D:\llama.cpp\scripts\stop-llama.ps1      # stop cleanly (frees all VRAM)
D:\llama.cpp\scripts\restart-llama.ps1   # stop then start (picks up models.ini changes)
D:\llama.cpp\scripts\unload-llama.ps1    # free VRAM but keep the server running (for another GPU task)
D:\llama.cpp\scripts\load.ps1 <name>     # switch the loaded model (fuzzy match); starts server if down
```

- API base URL: **`http://127.0.0.1:8080/v1`** (any non-empty API key)
- Built-in web UI: open **`http://127.0.0.1:8080`** in a browser (has a model dropdown)
- Switch models by setting the request's `model` field to an id from `GET /v1/models`, or use `load.ps1`
- Windows tray app: `tray\LlamaTray.lnk` — GUI equivalent of these scripts (start/stop/restart/load/unload)
  with a tray icon reflecting live server/model state; see `tray/LlamaTray/`.

## Layout

```
D:\llama.cpp\
  src\            cloned ggml-org/llama.cpp + build\bin\ (llama-server.exe, ...)
  models\         GGUFs, one folder per model:  <publisher>__<repo>\*.gguf
  models.ini      per-model config (context, batch/ubatch, KV quant, lite preset, ...)
  scripts\
    start-llama.ps1 launch router server (hidden), waits for health
    stop-llama.ps1      tree-kill the server (avoids orphaned VRAM)
    restart-llama.ps1   stop then start (for config/model.ini reloads)
    unload-llama.ps1    unload all models to free VRAM, server stays up
    load.ps1        switch the loaded model (fuzzy match / menu), starts server if needed
    bench.ps1        pick a model and benchmark it (llama-bench)
    probe-ctx.ps1    find each model's max context via llama-bench (used to build models.ini)
    update.ps1       git pull + recompile the CUDA build, then restart
  tray\           Windows tray app (GUI equivalent of the scripts above)
  README.md       this file
  server.out.log / server.err.log   runtime logs
```

## Hardware / toolchain (as built)

- GPU: **NVIDIA RTX 5090, 32 GB** (Blackwell, compute capability **sm_120**).
- Preinstalled, no downloads needed: CUDA Toolkit **13.3**, MSVC (VS 18 Community), CMake + Ninja (bundled in VS), git.
- Measured: 27B Q4_K_M ≈ **72 tokens/sec** eval, fully GPU-offloaded.

## Updating

```powershell
D:\llama.cpp\scripts\update.ps1              # stop server, git pull, rebuild, restart
D:\llama.cpp\scripts\update.ps1 -NoRestart   # update + rebuild but leave the server stopped
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

`models.ini` is the source of truth for the current model list and per-model config (ctx-size,
KV quant, ubatch, etc.) — each `[section]` header there is a model id usable in the API `model`
field. Don't duplicate those values here; read `models.ini` directly, or `GET /v1/models` on the
running router.

### Adding a model

Router discovery is **one level deep only**: `models\<name>\*.gguf`, pairing the model gguf with an
`mmproj*.gguf` in the same folder. LM Studio's two-level `<publisher>\<repo>\` layout yields **0 models** —
that's why everything was flattened to `<publisher>__<repo>\`. Drop a new model in its own folder under
`models\`, then restart the server. The folder name becomes the model id.

## Per-model configuration — `models.ini`

Referenced by `scripts\start-llama.ps1` via `--models-preset`. INI with a `[*]` global section plus one
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

`scripts\probe-ctx.ps1` binary-searches each model's largest context that loads (via `llama-bench`, which self-exits
per probe). Its numbers are single-sequence and slightly optimistic vs the router, so always confirm by
actually loading through the router — the values in `models.ini` are router-verified.

## Switching the loaded model

```powershell
D:\llama.cpp\scripts\load.ps1            # numbered menu of all models
D:\llama.cpp\scripts\load.ps1 gemma      # fuzzy match (careful: "lite" also matches "ab-lite-rated")
D:\llama.cpp\scripts\load.ps1 8b-lite    # loads qwen3-vl-8b-lite
```

Starts the server if it isn't running, then POSTs `/models/load` and **waits** for the load to finish
(the endpoint is async) before reporting VRAM. Alternatives: the **web UI dropdown** at
`http://127.0.0.1:8080`, selecting the model in your client (pi), or just sending a chat request with
a new `model` value (lazy auto-load). Under `--models-max 1` any of these evicts the current model.

## Freeing VRAM for another GPU task

To reclaim VRAM without stopping the server:

```powershell
D:\llama.cpp\scripts\unload-llama.ps1     # unloads all loaded models, server stays up
```

`/models/unload` unloads **one named model per call** (there's no single "unload all" endpoint), so the
script iterates every model id and unloads whatever is running. Manual equivalent:

```sh
curl -X POST http://127.0.0.1:8080/models/unload -H "Content-Type: application/json" -d '{"model":"<id>"}'
```

**Caveat:** `--models-autoload` is on, so the **next API request reloads a model** and re-takes VRAM. While
your other GPU task runs, don't send requests to `:8080`. For a hard guarantee that nothing reloads, use
`scripts\stop-llama.ps1` instead and `scripts\start-llama.ps1` afterwards.

## Benchmarking

`bench.ps1` wraps `llama-bench` (the CLI benchmark tool): it lists your models, lets you pick one,
applies that model's `cache-type-k/v` from `models.ini`, frees VRAM from the running server first
(so it won't OOM), then reports prompt-processing (`pp`) and token-generation (`tg`) speed in t/s.

```powershell
D:\llama.cpp\scripts\bench.ps1                                   # interactive menu (512 prompt / 128 gen)
D:\llama.cpp\scripts\bench.ps1 -Model lmstudio-community__Qwen3-VL-8B-Instruct-GGUF
D:\llama.cpp\scripts\bench.ps1 -PromptTokens 2048 -GenTokens 256 -Depth 4096 -Reps 5
```

Params: `-Model <id>` (skip the menu), `-PromptTokens` (`-p`), `-GenTokens` (`-n`), `-Depth` (`-d`,
speed at a given context depth), `-Reps` (`-r`), `-NGL`, `-FlashAttn on|off|auto`, `-NoUnload`.

`llama-bench` loads its **own** copy of the model (it does not use the router), which is why the script
frees the server's VRAM first. Reading `pp` = prefill/prompt throughput, `tg` = generation throughput.

## Auto-start at login

Handled by the **tray app** (`tray\LlamaTray.lnk`, see below): a shortcut in the Startup folder launches
it at login, and it starts the server itself if it isn't already running. This replaced the old
`llama-server` Task Scheduler entry (unregistered) that used to run `start-llama.ps1` directly.

## Tray app

`tray\LlamaTray\` is a WinForms system tray app — a GUI equivalent of the scripts above. It shows a
tray icon reflecting live state (stopped / running-no-model / model-loaded, with the model name in the
tooltip) and a right-click menu for Start/Stop/Restart/Load Model/Unload All/Open Web UI. It talks to the
router's REST API directly (`/health`, `/models`, `/models/load`, `/models/unload`) rather than shelling
out to the `.ps1` scripts. Settings (port, paths, `models-max`) live in `tray\LlamaTray\appsettings.json`.

```powershell
tray\LlamaTray.lnk   # launch the published exe (also what the Startup shortcut points at)
```

To rebuild after changes: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` from `tray\LlamaTray\`.

## pi integration

`~/.pi/agent/models.json` → provider `llama-local` points at `http://127.0.0.1:8080/v1`.
Each entry's **`id` must exactly match the router id** (the flat folder names above). pi only lists the
models defined there (not raw `/v1/models`), so a new preset needs an entry before it is selectable.
`apiKey` is the dummy literal `not-required` — the router needs no key, but pi hides models whose
provider has no auth at all. pi's built-in `llama.cpp` provider (`/login llama.cpp`, `/llama`) is a
separate, dynamic integration that only shows *loaded* models; this setup deliberately does not use it.

## Gotchas / lessons learned

- **Stopping: never kill the port PID alone.** It orphans the model child process, which keeps holding
  VRAM (saw ~30 GB stuck). Always use `scripts\stop-llama.ps1` (does `taskkill /PID <pid> /T /F`) or the
  tray app's Stop, or `POST /models/unload` to free VRAM without stopping the server.
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
