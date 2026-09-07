# Mirrored external configs (moved out of AGENTS.md)

Several configs outside this repo duplicate each model's id and **context
size** so that other tools can talk to Unsloth
(`http://127.0.0.1:8888/v1`). The `update-model-configs` skill discovers the
model list from `GET /v1/models`; it no longer uses `models.ini` as the list
source. Context and capability metadata are preserved for existing entries,
and new endpoint models receive conservative `8192` text-only defaults until
reviewed.

## VS Code

`C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` — VS Code
chat model list. Each entry's `maxInputTokens` should equal
`ctx-size - maxOutputTokens` (output is `8192` by default) to match the
corresponding `models.ini` section.

## pi

`C:\Users\Chris\.pi\agent\models.json` — pi's `unsloth` provider.
Each entry under `providers.unsloth.models[]` keys by `id` (the
`models.ini` section name); `contextWindow` = that section's `ctx-size`.
`maxTokens` is a deliberate per-tool choice, not derived (currently `65536`
everywhere) — leave it alone unless asked. `apiKey` is a dummy literal
(`not-required`): the router runs without `--api-key`, but pi hides a model
from `/model` unless *some* auth is configured for its provider, so don't
remove it. `input` (vision) and `reasoning`/`compat.thinkingFormat` are
**not** auto-derived by the sync skill — set by hand per model, mirroring
the section's `mmproj` line and its `reasoning = on` flag.

pi also ships a **built-in** `llama.cpp` provider (`/login llama.cpp`, driven
by `/llama`) that auto-discovers from the router. It is deliberately unused:
it lists only *loaded* models and hardcodes `reasoning: false`. Don't
"simplify" the static `unsloth` list away in favour of it.

## OpenCode

`C:\Users\Chris\.config\opencode\opencode.json` — OpenCode's static
`llama-local` provider. Each entry under `provider.llama-local.models{}` keys
by model id; `models[id].limit.context` equals that section's `ctx-size` and
`limit.output` remains an explicit per-tool choice.

## General

- Entries key by the same model id used in `models.ini` (the `[section]`
  name). Adding, removing, or renaming a model in `models.ini` should be
  mirrored in the VS Code list as well, not just context-size edits.
- KV cache quant (`cache-type-k`/`cache-type-v`) is router-internal and does
  **not** need mirroring here on its own — these configs only track
  `ctx-size` and the model list. Only touch them for a KV-quant-only change
  if it also changes the max `ctx-size` that fits in VRAM.
