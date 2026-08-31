# Instructions for this workspace (root)

Chris's personal local-inference setup built on top of the vendored
`llama.cpp` upstream checkout in `src/` (its [src/AGENTS.md](src/AGENTS.md)
is upstream's contributor policy, not relevant here). Detailed reference:
[docs/workspace-reference.md](docs/workspace-reference.md).

## Layout

- `models.ini` — per-model presets for `llama-server` router mode
  (`--models-preset`). Section name = model id = folder under `models/`.
  Precedence: CLI args > `[model-id]` > `[*]`.
- `models/` (one folder per model), `templates/` (chat templates referenced
  by `chat-template-file`), `drafters/` (speculative draft models loaded via
  `spec-draft-model`), `mmproj/` (loose vision projectors). The router only
  auto-scans `models/`, so the drafter and projector files live outside it
  and are reached by absolute path from `models.ini`.
- `scripts/` — `start/stop/restart-llama.ps1`, `load.ps1`, `bench.ps1`,
  `bench-spec.ps1`/`bench-dflash2.ps1`, `probe-ctx*.ps1`, `update.ps1`.
  See the reference doc for what each does.
- `tray/LlamaTray/` — C# tray app wrapping the same router (starts
  `llama-server.exe` with no per-model flags; those come from `models.ini`).
  Log: one file, whatever `LogFile` resolves to (`server.log` at the repo
  root right now). See the reference doc for the override chain.

## Rules

- **Downloading models**: use `uvx hf download <repo> <file> --local-dir
  models/<model-id>` (never bare `hf` or raw `curl`); always verify the
  finished file's size against the repo's authoritative `Content-Length`.
  Details in the reference doc.
- **Context sizing**: use `scripts/probe-ctx-headroom.ps1`. Idle probes
  over-report free VRAM — pad the headroom target and re-check `nvidia-smi`
  during a real generation before treating a context as final.
- **Mirrored external configs**: whenever a model's `ctx-size` (or the model
  list) changes in `models.ini`, also update the VS Code chat model list
  (`C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json`) and
  pi's `llama-local` provider (`C:\Users\Chris\.pi\agent\models.json`).
  Use the `update-model-configs` skill; invariants and details in
  [docs/related-tools.md](docs/related-tools.md).
