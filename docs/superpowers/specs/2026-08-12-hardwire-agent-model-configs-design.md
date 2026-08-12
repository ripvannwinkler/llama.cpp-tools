# Hardwire opencode/pi model configs (drop auto-discovery)

## Context

Three external tools mirror this repo's `models.ini` router presets so their
model pickers work against `http://127.0.0.1:8080/v1`: VS Code, opencode, and
pi. VS Code's `chatLanguageModels.json` has always been hand-maintained
(`AGENTS.md`'s "Related tools" section, kept in sync by the
`update-model-configs` skill). opencode and pi were instead wired for
**live auto-discovery**: an opencode plugin queries `GET /v1/models` at every
startup and injects a model map into `opencode.json`; pi's installed
`pi-llama-cpp` extension does the same live query on every `/models`/`/llama`
interaction.

The user wants this reversed: opencode and pi should carry the same kind of
hand-maintained, static model list VS Code already has, instead of depending
on the router responding correctly at startup/query time. This also closes a
real gap — only VS Code was actually being kept in sync by
`update-model-configs`; opencode/pi were assumed to "just work" via discovery
and were never covered by that skill's registry.

## Current state (confirmed by reading each tool's config/source)

- **opencode** (`C:\Users\Chris\.config\opencode\opencode.json`): the
  `llama-local` provider has no `models` key at all. A plugin symlinked at
  `~/.config/opencode/plugins/llama-router-discovery.js` (source:
  `E:\01-personal\opencode-model-discovery`) injects `config.provider.llama-local.models`
  from `GET {baseURL}/models` on every startup, parsing context size out of
  each model's `status.args` (`--ctx-size`).
- **pi**: two *separate* llama.cpp integrations exist, both live/dynamic —
  neither is what we want:
  - pi core's **built-in** `llama.cpp` provider (`/login llama.cpp`, managed
    via `/llama`) — already has credentials in `~/.pi/agent/auth.json` under
    key `"llama.cpp"`, unused (leave as-is, don't touch).
  - The **`pi-llama-cpp` extension** (npm package, listed in
    `~/.pi/agent/settings.json`'s `packages`) — registers provider id
    `llama-server=http://127.0.0.1:8080`, currently `settings.json`'s
    `defaultProvider`. This is what's actually in active use today.
  - `~/.pi/agent/models.json` (currently `{ "providers": {} }`) is pi core's
    **native static-provider mechanism** — fully documented, independent of
    both integrations above. Any provider defined here with `api:
    "openai-completions"` is a plain hardcoded model list; pi never queries
    the server for capabilities.
- **VS Code** (`chatLanguageModels.json`): already static, no change needed
  beyond what the earlier task in this conversation already did (added the
  new `Qwen3.6-35B-A3B-MXFP4_MOE-BF16` entry).

## Design

### pi

1. Add a brand-new custom provider to `~/.pi/agent/models.json`:
   ```json
   {
     "providers": {
       "llama-local": {
         "baseUrl": "http://127.0.0.1:8080/v1",
         "api": "openai-completions",
         "apiKey": "not-required",
         "models": [ /* one entry per models.ini section, see table below */ ]
       }
     }
   }
   ```
   `apiKey` is a dummy literal — the router runs without `--api-key`
   (confirmed in `scripts\start-llama.ps1`), but pi hides a model from
   `/model` unless *some* auth is configured for its provider.

2. Uninstall the extension: `pi remove npm:pi-llama-cpp` (removes it from
   `settings.json`'s `packages` array). This also removes its
   `llama-server=...` provider — the live load/unload/switch UI it gave up
   goes away. Equivalent functionality already exists in this repo:
   `scripts\load.ps1` / `unload-llama.ps1`, and LlamaTray for start/stop.

3. Update `~/.pi/agent/settings.json`: `defaultProvider` →
   `"llama-local"`, `defaultModel` → keep `"Gemma-4-31B-it-QAT-Abliterated"`
   (same model, new provider id).

4. Leave `auth.json`'s existing `"llama.cpp"` entry untouched — it's for the
   unused built-in provider, harmless, out of scope.

### opencode

1. Remove the plugin symlink:
   `~/.config/opencode/plugins/llama-router-discovery.js`. (Leave the source
   repo at `E:\01-personal\opencode-model-discovery` alone — just unhook it.)
2. Add a static `models` map to `opencode.json`'s `llama-local` provider,
   same shape the plugin used to generate:
   ```json
   "models": {
     "<models.ini section>": { "name": "<friendly name>", "limit": { "context": <ctx-size>, "output": 8192 } }
   }
   ```

### Model list (both configs, derived from `models.ini`)

| id | ctx-size | vision/image | reasoning |
|---|---|---|---|
| `Qwen3.6-27B-NVFP4` | 262144 | yes | yes (`qwen-chat-template`) |
| `Qwen3.6-35B-A3B-UD-Q4_K_XL_MTP` | 262144 | yes | yes (`qwen-chat-template`) |
| `Qwen3.6-35B-A3B-MXFP4_MOE-BF16` | 262144 | no | yes (`qwen-chat-template`) |
| `Gemma-4-31B-it-QAT-Abliterated` | 102400 | yes | no |
| `Muse-Glimmer-30B-Abliterated-Q5_K_M` | 131072 | no | no |

Notes on how these were derived (not re-guessed from scratch):
- **`reasoning`**: mirrors `models.ini`'s `reasoning = on` flag exactly — only
  the three Qwen3.6 presets have it. For pi, that's `reasoning: true` +
  `compat: { thinkingFormat: "qwen-chat-template" }`, matching the pattern
  `AGENTS.md` already documents for this model family (avoids the
  `reasoning_effort`-silently-ignored trap the `llamacpp` skill warns about —
  llama.cpp needs `chat_template_kwargs.enable_thinking`, not
  `reasoning_effort`). Gemma and Muse-Glimmer are left `reasoning: false`
  (pi default) even though Muse-Glimmer is documented to always emit a
  `reasoning_content` trace regardless of any flag — the correct
  `thinkingFormat` for its template family (Gemma3-based "Onyx ATEM") isn't
  verified, so this is called out as a known follow-up rather than guessed.
  opencode's plugin never modeled reasoning at all, so this only affects pi.
- **vision/image `input`**: mirrors the **existing, hand-validated**
  `chatLanguageModels.json` `vision` flags, not a fresh re-derivation from
  "does the `models.ini` section have an `mmproj` line" (the
  `update-model-configs` skill's stated heuristic). Two entries
  (`Qwen3.6-27B-NVFP4`, `Qwen3.6-35B-A3B-UD-Q4_K_XL_MTP`) are marked
  vision-capable in VS Code despite no `mmproj` line in their `models.ini`
  section, and `Muse-Glimmer-30B-Abliterated-Q5_K_M` is marked
  vision **false** in VS Code despite *having* a wired `mmproj` line —
  contradicting that heuristic in both directions. Since the VS Code flags
  are the product of actual testing (per `AGENTS.md`'s notes), this design
  treats them as ground truth and mirrors them as-is. This pre-existing
  three-way inconsistency (skill heuristic vs. `models.ini` vs. VS Code
  flags) is flagged here for awareness, not fixed — out of scope for this
  change.
- `maxTokens`/output: `8192` everywhere, matching the VS Code convention
  already documented in `AGENTS.md`.

### Keeping this in sync going forward

Extend `update-model-configs` (the skill file and `AGENTS.md`'s "Related
tools" registry table) with two more targets:

| Config file | Model list path | Context field |
|---|---|---|
| `C:\Users\Chris\.config\opencode\opencode.json` | `provider.llama-local.models{}` | `models[id].limit.context` |
| `C:\Users\Chris\.pi\agent\models.json` | `providers.llama-local.models[]` | `models[].contextWindow` |

Same reconciliation rules as the existing VS Code target (add/remove/rename
by `models.ini` section, recompute context from `ctx-size`, leave
`maxTokens`/`output` alone unless deliberately changed). `vision`/`input` and
`reasoning`/`compat` are **not** auto-derived by the skill (per the notes
above, both have known heuristic gaps) — flag any new/changed preset's likely
capability but leave the final call to the user, same as the skill already
does by deferring to hand-set `toolCalling` for VS Code.

## Verification

- `pi --list-models llama-local` (or `/model` inside pi) shows exactly the 5
  models above with the right context sizes, with no live network call to
  the router required for the list to populate (test by stopping the router
  first — the model list must still appear, only load/inference would fail).
- `opencode models llama-local` shows the same 5, matching `models.ini`.
- Confirm `pi-llama-cpp` no longer appears in `pi list`, and the
  `llama-router-discovery.js` symlink is gone from
  `~/.config/opencode/plugins/`.
