# Local llama.cpp (RTX 5090)

Vendored `llama.cpp` checkout in `src/`, built for CUDA on an RTX 5090, plus the
model-config sync used by the tools that talk to the running server.
Everything lives under `D:\llama.cpp\`. Agent-facing rules: [AGENTS.md](AGENTS.md).

## Hardware / toolchain (as built)

- GPU: **NVIDIA RTX 5090, 32 GB** (Blackwell, compute capability **sm_120**).
- Preinstalled, no downloads needed: CUDA Toolkit **13.3**, MSVC (VS 18 Community), CMake + Ninja (bundled in VS), git.

## Updating

```powershell
D:\llama.cpp\scripts\update.ps1              # rebase, rebuild, restart server if it was up
D:\llama.cpp\scripts\update.ps1 -NoRestart   # update + rebuild but leave the server stopped
```

`update.ps1` refuses to run on a dirty `src/` tree, switches to `feat/custom`, fetches and rebases
onto `origin/master`, stops a running server (a live `llama-server.exe` locks the binary so the link
step would fail), does an **incremental** CUDA rebuild in the VS dev environment, prints old→new
version, and restarts the server if it was running.

## Building / rebuilding (manual)

Run from an **"x64 Native Tools Command Prompt for VS 18"** (or a shell that has run
`vcvars64.bat`), so `cl.exe` + CUDA are on PATH.

```sh
cd D:\llama.cpp\src
git fetch origin && git rebase origin/master
cmake -B build -G Ninja -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release -DCMAKE_CUDA_ARCHITECTURES=120
cmake --build build --config Release -j
```

- `-DGGML_CUDA=ON` is the key flag; `120` = sm_120 (RTX 5090). Pass the arch explicitly rather than
  relying on auto-detection.
- ccache is enabled by default upstream but **not installed here**, so every build recompiles the CUDA
  kernels from scratch.
- Confirm it's a CUDA build: `src\build\bin\llama-server.exe` startup log shows
  `CUDA0: NVIDIA GeForce RTX 5090`, and `src\build\bin\ggml-cuda.dll` exists.

## Model config sync

The served model list comes from the Unsloth app's OpenAI-compatible endpoint at
`http://127.0.0.1:8888/v1`. Three external configs mirror it — VS Code's chat model list, pi's
`unsloth` provider (plus its `settings.json` thinking levels), and OpenCode's `llama-local`
provider. Use the `update-model-configs` skill; exact file paths and per-field mapping rules are
in [docs/related-tools.md](docs/related-tools.md).

## References

- Build docs: https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md
- Server / router docs: `src\tools\server\README.md`
