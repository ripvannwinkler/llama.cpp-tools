---
name: update-chat-templates
description: >
  Update the locally installed Qwen-Sharp chat template(s) in templates/ to the
  latest version from the peculiar-ragdoll/Qwen-Sharp-Chat-Templates HF repo.
  Use whenever the user says "update chat templates", "update the qwen template",
  "check for template updates", or after a models.ini change that references a
  Qwen-Sharp template — also worth running periodically, since upstream rebases
  onto froggeric fixes regularly.
---

# update-chat-templates

Keep the locally downloaded copies of community chat templates current with
their upstream HF repos.

## Templates tracked

| Local file (under `D:\llama.cpp\templates\`) | Upstream repo | Upstream file |
|---|---|---|
| `Qwen-Sharp-Chat-Template.jinja` | `peculiar-ragdoll/Qwen-Sharp-Chat-Templates` | `chat_template.jinja` |

Re-read this table's upstream repo each run — if a new sibling template is
installed locally and wired into `models.ini` via `chat-template-file`, add it
here. Do NOT touch templates that aren't derived from an external repo
(`Qwen3-Fixed-Chat-Template.jinja`, `Gemma31b_fixed_chat_template.jinja`,
`Muse-Glimmer-Abliterated-Chat-Template.jinja` etc. are local/untracked).

## Version detection

The installed file's first line contains its version tag, e.g.
`{%- set template_version = "qwen3.8-froggeric-v22.3.1" %}`. The README of the
upstream repo states the current version at top. Compare before downloading;
skip everything if already current.

Note the version-string gotcha documented upstream: e.g. `v22.3.1` does not
contain `v22.1` as a substring, so naive substring matching against old ids can
misreport. Compare parsed versions, not substrings.

## Procedure

1. Fetch the upstream repo's file listing / README to determine the latest
   version (webfetch `https://huggingface.co/<repo>/tree/main`).
2. Read line 1 of each local file; report current vs latest.
3. If outdated:
   - Back up the local copy to `<name>.v<OLDVER>.jinja.bak` (overwrite any
     previous backup of the same version).
   - Download via `uvx hf download <repo> <file> --local-dir
     "$env:TEMP\opencode\<slug>"`, then copy over the local file. (`uvx hf`,
     not bare `hf`; no global install exists.)
   - **Verify size** against the authoritative `Content-Length` from
     `curl.exe -sIL https://huggingface.co/<repo>/resolve/main/<file>` — follow
     to the final non-redirect response. A clean exit code alone does not rule
     out truncation. Sizes must match byte-exact.
4. Confirm the new file's first-line `template_version` matches what was
   expected.

## After updating

- `models.ini` needs NO edit — presets reference the fixed path
  `D:\llama.cpp\templates\...`, so the swap takes effect on next model load /
  router restart (`scripts\restart-llama.ps1`). Say so explicitly.
- Per repo convention: recommend smoke-testing one affected preset (a tool-call
  request + a reasoning request) before trusting it in agent mode. The template
  injects an opinionated terseness system prompt when no system message is sent
  — flag that behavior change if the version bump changes it (the README's
  changelog section lists behavioral notes per release).
- Offer (don't auto-run) the `update-model-configs` skill only if model ids or
  ctx-sizes changed — a template-only update never affects those configs.

## Notes

- Never edit the downloaded template content by hand; it would be overwritten
  by the next update.
- Old `.bak` files are deliberate rollback points — leave them on disk unless
  the user asks to clean up.
