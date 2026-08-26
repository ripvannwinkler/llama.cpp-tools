---
name: update-model-configs
description: >
  Sync the external tool configs that mirror this repo's llama.cpp router model
  list after models.ini changes. Use whenever a model's ctx-size changes, or a
  model is added / removed / renamed in models.ini — it propagates the model id
  list and context size into the VS Code chat model list and the opencode
  config (and any other related-tool configs listed in AGENTS.md). Trigger on:
  "update model configs", "sync the related tools", "I changed models.ini",
  "propagate ctx-size", after editing a preset's ctx-size or the [section] list.
---

# update-model-configs

Propagate model changes from `models.ini` (the source of truth) into the
external tool configs that duplicate the router's model list. These configs live
**outside this repo** (absolute Windows paths) and are not under git here, so
there is nothing to commit for them — just edit in place.

## Source of truth

`D:\llama.cpp\models.ini` — each `[section]` is a model id (= folder name under
`models/`). The value to propagate is that section's `ctx-size`, falling back to
the `[*]` global (`8192`) if a section has no explicit `ctx-size`. The set of
`[section]` names is the authoritative model list. Ignore `[*]` itself — it is
the global default, not a model.

Do **not** propagate KV-quant changes (`cache-type-k` / `cache-type-v`),
`batch-size`, `ubatch-size`, `spec-type`, templates, etc. — the related configs
only track the **model id list** and **context size**. (Exception: a KV-quant
change that also changes the max `ctx-size` that fits in VRAM — then it's really
a ctx-size change and does propagate.)

## Targets — read AGENTS.md first

`AGENTS.md` → section **"Related tools — update whenever model params change"**
is the registry of target files and the exact mapping rule for each. Re-read it
every run so a newly added tool (e.g. a Claude CLI config) is picked up — do not
rely solely on the list baked in below. As of this writing the targets are:

| Config file | Model list path | Context field | Value = |
|---|---|---|---|
| `C:\Users\Chris\AppData\Roaming\Code\User\chatLanguageModels.json` (VS Code chat) | `[0].models[]`, keyed by `id` | `maxInputTokens` | `ctx-size − maxOutputTokens` (that entry's own output, default `8192`) |
| `C:\Users\Chris\.config\opencode\opencode.json` | `provider.llama-local.models{}`, keyed by object key | `models[id].limit.context` | `ctx-size` (no output subtraction — opencode tracks context and output separately, unlike VS Code's combined budget) |

For opencode, `vision`/`input` is **not** mechanically derived — flag the
likely value for a new/changed preset (mirror `chatLanguageModels.json`'s
`vision` flag) but leave the final call to the user, the same way
`toolCalling` is already handled for VS Code.

All entries key by the **exact** `models.ini` section name (including
spaces/parens, e.g. `Qwen3-VL-8B-Instruct (Lite, Uncensored)`).

### opencode `name` field must stay short

In `opencode.json`, the object key / `id` must stay **byte-identical** to the
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

| models.ini id | opencode `name` |
|---|---|
| `Qwen3.8-27B-NVFP4-MTP-VERY-HIGH` | `Qwen3.8 27B` |
| `Gemma-4-26B-A4B-it-UD-Q6_K` | `Gemma-4 26B-A4B` |
| `Muse-Glimmer-30B-Abliterated-Q4_K_M` | `Muse-Glimmer 30B` |

**Collision rule.** If two presets would derive the same `name`, append the
minimum distinguishing qualifier from the id (usually the quant tag) to the
newer one, and call it out in the run report — it deliberately re-lengthens a
name.

Why: the model-name column in the opencode UI (and the model picker /
reasoning-effort dropdown) clips long names, and every qualifier appended
(`UD-Q4_K_M`, `MTP`, `vision`, `tools`, `reasoning: …`) pushes the
reasoning-effort control off-screen. Never build a long descriptive `name` like
`Qwen3.8 27B (stock, UD-Q4_K_M, MTP, vision, reasoning: low)`. Only a human
should ever decide to widen a name beyond the rule above.

## Procedure

1. Parse `models.ini`: build a map of `{ section-name → ctx-size }` (apply the
   `[*]` fallback). This is the desired model set.
2. For each target file, read it and reconcile against that map:
   - **Model list.** Every models.ini section must have exactly one entry.
     - Missing → add an entry, copying the shape of a sibling entry (same
       `url`/provider fields, `vision`/`input`/`toolCalling`
       per the model's real capabilities — check the models.ini preset for an
       `mmproj` line to decide vision/image support). For opencode's `name`,
       apply the derivation rule in "opencode `name` field must stay short"
       above — do not copy the id verbatim.
     - Present in the config but gone from models.ini → remove it.
     - Renamed → treat as remove-old + add-new (ids must match exactly).
   - **Context field.** Set it per the mapping table above for every entry.
3. **Output-token fields are NOT derived from models.ini.** `maxOutputTokens`
   encodes a deliberate per-tool choice. Leave it as it is. The one exception:
   VS Code's `maxInputTokens` **is** derived (`ctx-size − maxOutputTokens`), so
   recompute it whenever either input changes, using that entry's existing
   `maxOutputTokens`.
4. Edit the JSON in place with targeted replacements — do not regenerate/reindent
   the whole file. Preserve existing key order, 2-space indentation, and any
   blank lines so the diff stays minimal.

## Verification

After editing, re-read each target and confirm:
- Its set of model ids matches the `models.ini` sections exactly (no extras, none
  missing).
- Each entry's context field equals the expected value from the mapping table.

Report a table: `model id | models.ini ctx-size | VS Code maxInputTokens`
(VS Code column = ctx − that model's output).

## Notes

- These files are OUTSIDE `D:\llama.cpp`; they are not committed by this repo.
  Only `models.ini` / repo files get committed — and only if the user asked.
- If a target file is missing or unreadable, report it and skip that target
  rather than failing the whole run.
- Watch for the common drift: a preset whose `ctx-size` was bumped in models.ini
  but not mirrored (e.g. `Qwen3.6-27B-UD-Q4_K_XL` left at an old `131072`). That
  causes prompt truncation / errors in the downstream tool, which is the whole
  reason this sync exists.
