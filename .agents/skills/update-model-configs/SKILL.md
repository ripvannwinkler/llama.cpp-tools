---
name: update-model-configs
description: >
  Sync external tool configs from the Unsloth model endpoint with interactive
  model selection. Use whenever the served model list changes or a related-tool
  config needs reconciliation. The endpoint supplies model ids; you select which
  models to sync, and all others are removed from the configs. Trigger on:
  "update model configs", "sync the related tools", or "refresh the Unsloth model list".
---

# update-model-configs

Propagate model changes from Unsloth's `/v1/models` endpoint into the
external tool configs, with **interactive model selection**. You choose which
models to sync; all others are removed from the configs. These configs live
**outside this repo** (absolute Windows paths) and are not under git here, so
there is nothing to commit for them — just edit in place.

## Source of truth

Unsloth's OpenAI-compatible endpoint is the source of truth:
`GET http://127.0.0.1:8888/v1/models`. Use each returned `data[].id` as the
model list for the selection prompt. Do not enumerate model sections from
`models.ini`.

The endpoint currently does not expose context size, vision, or reasoning
metadata. For selected entries, use the tool's existing fallback context
(`8192` for VS Code's input-budget calculation and `8192` for pi/OpenCode
context) and conservative text-only, non-reasoning capabilities; flag these
defaults in the report for manual review.

Do **not** propagate KV-quant changes (`cache-type-k` / `cache-type-v`),
`batch-size`, `ubatch-size`, `spec-type`, templates, etc. — the related configs
only track the **model id list** and **context size**. (Exception: a KV-quant
change that also changes the max `ctx-size` that fits in VRAM — then it's really
a ctx-size change and does propagate.)

## Targets — read docs/related-tools.md first

`docs/related-tools.md` (section **"Mirrored external configs"**) is the
registry of target files and the exact mapping rule for each. Re-read it
every run so a newly added tool (e.g. a Claude CLI config) is picked up — do not
rely solely on the list baked in below. As of this writing the targets are:

| Config file | Model list path | Context field | Value = |
|---|---|---|---|
| `C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` (VS Code chat) | `[0].models[]`, keyed by `id` | `maxInputTokens` | `ctx-size − maxOutputTokens` (that entry's own output, default `8192`) |
| `C:\Users\Chris\.pi\agent\models.json` (pi) | `providers.unsloth.models[]`, keyed by `id` | `contextWindow` | `ctx-size` (no output subtraction — pi tracks `contextWindow` and `maxTokens` separately, unlike VS Code's combined budget) |
| `C:\Users\Chris\.pi\agent\settings.json` (pi defaults) | `modelThinkingLevels`, keyed by `provider/model-id` | per-model thinking level | keep entries aligned with the current pi model list; `defaultThinkingLevel` is the fallback/default and must be reviewed when model capabilities change |
| `C:\Users\Chris\.config\opencode\opencode.json` (OpenCode) | `provider.llama-local.models{}`, keyed by the exact model id | `models[id].limit.context` | `ctx-size` (leave `limit.output` unchanged); `models[id].name` must also be the exact model id |

### pi: thinking compatibility, levels, and capabilities are hand-set

When adding, removing, renaming, or materially changing a model, update both
pi files, not just `contextWindow`:

- In `models.json`, review `thinkingCompat` (or the installed schema's
  equivalent `compat`) and `thinkingLevelMap` for the model. These must describe
  the template's actual thinking interface: preserve the existing compatibility
  shape only when it is known to work, and do not copy a sibling's map blindly.
  Unsupported levels should be `null`; supported levels should map to the
  exact value accepted by the chat template/server.
- In `settings.json`, reconcile `modelThinkingLevels` using exact keys of the
  form `unsloth/<models.ini id>` (remove stale ids, add new ids, and retain
  deliberate per-model defaults). Review `defaultThinkingLevel` as well: it is
  the fallback for models without an explicit override, so it must be a level
  supported by the newly synced model set. Do not silently change a user's
  deliberate defaults; flag an ambiguous choice.

pi's static `unsloth` entries carry capability fields the sync step must
never write blind:

- `input`: `["text", "image"]` when that `models.ini` section has an `mmproj`
  line, `["text"]` otherwise. This should agree with
  `chatLanguageModels.json`'s `vision` flag and with the router's own
  `architecture.input_modalities` from `/v1/models`; if the three ever
  disagree, stop and report it rather than picking one.
- `reasoning`: mirrors the section's `reasoning = on` flag. Add
  `compat: { "thinkingFormat": "qwen-chat-template" }` only for presets on a
  Qwen-Sharp chat template — llama.cpp reads
  `chat_template_kwargs.enable_thinking`, not `reasoning_effort`, which the
  `llamacpp` skill warns is silently ignored. For other template families
  (Gemma, Muse-Glimmer) set `reasoning: true` with **no** `thinkingFormat`
  until the right one is verified.

Flag the likely value for a new/changed preset but leave the final call to the
user, the same way `toolCalling` is already handled for VS Code. Never *remove*
an existing `input`, `reasoning`, or `compat` while syncing context sizes.

Do not touch `apiKey` (`not-required`), `baseUrl`, or `api` — see
`docs/related-tools.md` for why the dummy key must stay. The `unsloth` base URL is managed outside
this skill and should point to Unsloth at `http://127.0.0.1:8888/v1`.

All entries key by the **exact** `models.ini` section name (including
spaces/parens, e.g. `Qwen3-VL-8B-Instruct (Lite, Uncensored)`). OpenCode must
use that exact string for both the model object key and its `name` field; never
use a shortened or display-only name there. (The shortened-name rule below
applies only to pi.)

### pi `name` field must stay short

In `models.json`, each entry's `id` must stay **byte-identical** to the
`models.ini` section name (that is what the router matches on), but the `name`
is display-only and must be **derived and shortened**:

> `name` = model family + version, one space, parameter count — nothing else.

Strip every trailing qualifier from the id: instruction-tune tag (`-it`),
quant (`Q4_K_M`, `UD-Q6_K`, `NVFP4`, `MXFP4_MOE-BF16`, `BF16`), `MTP`,
`Abliterated`/`abliterated`, `QAT`, `APEX`, quality tier (`Quality`,
`VERY-HIGH`, `Lite`, `Uncensored`), and any parenthesised suffix. Keep the MoE
active-parameter suffix (`-A3B`, `-A4B`) attached to the total, since it is part
of the parameter count.

Worked examples:

| models.ini id | pi `name` |
|---|---|
| `Qwen3.8-27B-NVFP4-MTP-VERY-HIGH` | `Qwen3.8 27B` |
| `Gemma-4-26B-A4B-it-UD-Q6_K` | `Gemma-4 26B-A4B` |
| `Muse-Glimmer-30B-Abliterated-Q4_K_M` | `Muse-Glimmer 30B` |

**Collision rule.** If two presets would derive the same `name`, append the
minimum distinguishing qualifier from the id (usually the quant tag) to the
newer one, and call it out in the run report — it deliberately re-lengthens a
name.

Why: the model-name column in the pi UI (and the model picker /
reasoning-effort dropdown) clips long names, and every qualifier appended
(`UD-Q4_K_M`, `MTP`, `vision`, `tools`, `reasoning: …`) pushes the
reasoning-effort control off-screen. Never build a long descriptive `name` like
`Qwen3.8 27B (stock, UD-Q4_K_M, MTP, vision, reasoning: low)`. Only a human
should ever decide to widen a name beyond the rule above.

## Procedure

### Phase 1 — Select models to sync

1. Query `GET http://127.0.0.1:8888/v1/models` and build the desired model set
   from `data[].id`. If the endpoint is unavailable or returns malformed data,
   stop without editing any target.
2. Present a **multi-select** question to the user, listing all endpoint model
   ids with brief descriptions (quant, vision, etc.). The user selects exactly
   which models to sync. All other models (present in the configs but not
   selected) will be **removed** from every target config file.
3. Record the user's selection. If the user selects zero models, stop without
   editing any target.

### Phase 2 — Sync selected models to each target

For each target file, read it and reconcile against the **user-selected** model
set (not the full endpoint set):

- **Model list.** Every selected endpoint model id must have exactly one entry.
  - Missing → add an entry, copying the shape of a sibling entry (same
    `url`/provider fields, `vision`/`input`/`toolCalling`
    per the model's real capabilities — check the models.ini preset for an
    `mmproj` line to decide vision/image support). For OpenCode, set both
    the object key and `name` to the exact models.ini section name. For pi's
    `name`,
    apply the derivation rule in "pi `name` field must stay short"
    above — do not copy the id verbatim.
  - Present in the config but **not selected** → remove it entirely.
  - Renamed → treat as remove-old + add-new (ids must match exactly).
- **Context field.** Use the documented `8192` fallback for all entries
  (selected or newly added), since Unsloth does not currently expose ctx-size.
- **OpenCode names.** For every present entry, set `models[id].name` to the
  exact `models.ini` section name as well; correct shortened or friendly
  names, including entries that otherwise need no change.
- **Thinking fields.** NOT derived from models.ini. Reconcile pi's
  `thinkingCompat`/`compat`, `thinkingLevelMap`, `settings.json`'s
  `modelThinkingLevels`, and the fallback `defaultThinkingLevel` as described
  above; verify template/server support and flag uncertain choices instead of
  guessing.
- **Output-token fields.** NOT derived from models.ini. `maxOutputTokens`
  encodes a deliberate per-tool choice. Leave it as it is. The one exception:
  VS Code's `maxInputTokens` **is** derived (`ctx-size − maxOutputTokens`), so
  recompute it whenever either input changes, using that entry's existing
  `maxOutputTokens`.

### Phase 3 — Edit configs in place

Edit the JSON in place with targeted replacements — do not regenerate/reindent
the whole file. Preserve existing key order, 2-space indentation, and any
blank lines so the diff stays minimal.

## Verification

After editing, re-read each target and confirm:
- Its set of model ids matches the **user-selected** ids exactly (no extras,
  none missing — i.e. the config now contains precisely the models the user
  chose).
- Existing context values were preserved for selected entries; new entries
  use the documented `8192` fallback and are flagged for review.
- OpenCode uses the exact models.ini section name as both each model key and
  its `name` value; no shortened aliases remain.
- pi `models.json` has reviewed `thinkingCompat`/`compat` and
  `thinkingLevelMap` values for every affected model.
- pi `settings.json` has no stale `modelThinkingLevels` keys (removed any keys
  for models not selected), includes every selected `unsloth` model where
  appropriate, and its `defaultThinkingLevel` is intentionally compatible with
  the model set.

Report a table: `model id | endpoint metadata | VS Code maxInputTokens`, and
flag any model using fallback metadata (all new/selected entries).

## Notes

- These files are OUTSIDE `D:\llama.cpp`; they are not committed by this repo.
  Only `models.ini` / repo files get committed — and only if the user asked.
- If a target file is missing or unreadable, report it and skip that target
  rather than failing the whole run.
- The multi-select prompt uses check-box style — the user can pick any subset
  of the available models. "Select all" and "Select none" options are available.
- This skill intentionally shifts from automatic sync to **guarded sync**,
  giving you control over which models appear in your tools.