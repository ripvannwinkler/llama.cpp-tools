# Instructions for this workspace (root)

Chris's local-inference setup, built on a vendored `llama.cpp` checkout in
`src/`. These notes cover only what this repo maintains: building `src/`,
and keeping the external tool configs in sync with the served model list.
Upstream's contributor policy is in [src/AGENTS.md](src/AGENTS.md).

## Building `src/`

- `src/` is a vendored clone of `ggml-org/llama.cpp`. Local patches live on
  `feat/custom`, rebased onto upstream `master`. (`feat/custom-legacy` is
  the older line, kept for reference.)
- `scripts/update.ps1` is the build entry point. It refuses to run on a
  dirty tree, switches to `feat/custom`, fetches and rebases onto
  `origin/master`, stops a running server (the binary is locked while
  running), then configures and builds incrementally inside the Visual
  Studio developer environment:

  ```
  cmake -B build -G Ninja -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_CUDA_ARCHITECTURES=120 -DGGML_CUDA_GRAPHS=ON \
        -DGGML_CUDA_FA_ALL_QUANTS=ON
  cmake --build build --config Release -j
  ```

- Output binary: `src/build/bin/llama-server.exe`.
- `-DCMAKE_CUDA_ARCHITECTURES=120` is pinned to this box's GPU; pass a
  different arch explicitly rather than relying on auto-detection.
- ccache is enabled by default but not installed here, so every build is a
  full rebuild of the CUDA kernels. Install ccache (and make sure it is on
  `PATH`) if that becomes painful.

## Syncing model configs

The served model list comes from the Unsloth app's OpenAI-compatible
endpoint at `http://127.0.0.1:8888/v1`. Three external configs mirror it:
VS Code's chat model list, pi's `unsloth` provider, and OpenCode's
`llama-local` provider. Use the `update-model-configs` skill; exact file
paths and per-field mapping rules are in
[docs/related-tools.md](docs/related-tools.md).
