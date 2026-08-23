---
name: remove-model
description: >
  Safely remove a llama.cpp router model from this local setup: remove its exact
  section from models.ini, reconcile every related-tool config, and permanently
  delete the matching model folder only after an explicit confirmation. Use this
  skill whenever the user asks to remove, delete, uninstall, retire, or clean up
  a model from the local llama.cpp model list, router, models.ini, or models
  directory—even if they do not say "remove-model". Always show a complete
  preview and obtain confirmation before making any destructive change.
---

# remove-model

Remove one model as a coordinated, confirmation-gated operation. The model id is
both the exact section name in `models.ini` and the exact folder name under
`D:\llama.cpp\models`.

This workflow is intentionally ordered: update the source of truth, synchronize
the external clients, then delete the disk files. Do not delete the folder if
configuration synchronization was incomplete or could not be verified.

## Scope and safety rules

- Treat the requested model id literally. Do not fuzzy-match, normalize, or
  guess between similarly named presets. If the request is ambiguous, ask for
  the exact `[section]` name first.
- `[*]` is the global defaults section, never a removable model. Refuse to
  remove it.
- The initial user request is not confirmation. First produce the preview below
  and wait for a separate affirmative reply that identifies the same model.
- Do not mutate `models.ini`, external JSON files, or the model directory during
  the preview. Do not use wildcards for the eventual deletion.
- Never delete a folder outside `D:\llama.cpp\models\<exact-model-id>`. Reject
  ids containing path traversal or path separators, and stop if the resolved
  target is a reparse point/symlink rather than a normal directory.
- Preserve unrelated working-tree changes. Before editing, inspect the relevant
  diff/status and do not overwrite or discard pre-existing edits.
- If the model is loaded or processing, do not remove its files. Ask the user to
  unload it or stop the router first, then re-check before deletion. A loaded
  model can keep files open or leave the router pointing at a path that no
  longer exists.

## Phase 1: inspect and preview

1. Read `D:\llama.cpp\AGENTS.md` and the current
   `D:\llama.cpp\models.ini`. Use `models.ini` as the source of truth; do not
   infer the id from a folder listing alone.
2. Find an exact, non-`[*]` section match. Capture the complete section text,
   its `ctx-size` (including the applicable `[*]` fallback), and whether it
   configures `mmproj`, a draft model, or other companion files.
3. Inspect `D:\llama.cpp\models\<model-id>` without changing it. Record whether
   it exists, whether it is a normal directory, and a complete recursive file
   inventory with sizes. If the directory is missing, report that fact but keep
   the config-removal plan separate; do not silently substitute another folder.
4. Check the running router, when available, to determine whether the exact
   model is loaded or processing. If the server cannot be queried, report that
   limitation and require it to be stopped before the deletion phase.
5. Read every related-tool target registered by the `AGENTS.md` section
   **Related tools — update whenever model params change**. Identify the exact
   entries that will be removed or reconciled. If a target is missing,
   unreadable, malformed, or has unexpected duplicate ids, make that a blocker
   for disk deletion and say so explicitly.
6. Show a concise but complete confirmation preview containing:
   - exact model id and matching `models.ini` section;
   - the `models.ini` section that will be removed;
   - each external config path and the matching entry/id that will disappear;
   - the exact disk path and recursive file count/size to be deleted;
   - whether the router must first be unloaded/stopped;
   - any blockers or uncertainties.

Ask for explicit confirmation after the preview. Use a clear prompt such as:

> This will remove `<model-id>` from `models.ini`, remove its matching entries
> from the related configs, and permanently delete
> `D:\llama.cpp\models\<model-id>` and all contents. Reply `confirm remove
> <model-id>` to proceed, or tell me to stop.

Accept an unambiguous affirmative reply that names the exact model, but do not
treat an unrelated “yes” as confirmation when more than one model or blocker is
involved.

## Phase 2: apply the confirmed removal

Proceed only after confirmation and only if the target is still unchanged.
Re-run the exact-id, path-safety, router-state, and working-tree checks so a
stale preview cannot authorize the wrong deletion.

### A. Remove the source section

Edit `D:\llama.cpp\models.ini` by removing only the exact `[model-id]` section
and its section body, up to (but not including) the next section header. Keep
all surrounding sections, comments, line endings, and formatting intact. Do not
remove `[*]` or any similarly prefixed section. Re-read the file and verify the
exact section is gone and every unrelated section remains.

If this edit cannot be made precisely, stop before touching external configs or
disk and report the failure.

### B. Synchronize related configs

Follow `D:\llama.cpp\.agents\skills\update-model-configs\SKILL.md` for the
reconciliation rules. In particular:

- Re-read `AGENTS.md` on this run to discover the complete current target
  registry.
- Reconcile each readable target against the post-removal `models.ini` model
  set. Remove the exact model id, remove no other model, and preserve JSON
  formatting/key order as that skill requires.
- Do not change unrelated capability flags, output-token choices, sampler
  settings, or other fields merely because this model is being removed.
- Re-read every edited target and verify exact model-id set equality and the
  expected remaining context values. Report missing/unreadable targets rather
  than pretending synchronization succeeded.

Do not proceed to disk deletion unless all registered, readable targets have
been successfully reconciled and verified. If synchronization fails after
`models.ini` was edited, leave the disk untouched, report the partial state, and
ask whether the user wants the config repair completed.

### C. Delete the model directory

Immediately before deletion, confirm again that:

- the router is stopped or the exact model is unloaded and not processing;
- `D:\llama.cpp\models\<exact-model-id>` still resolves to the same normal
  directory shown in the preview; and
- the directory is still a direct child of `D:\llama.cpp\models`.

Then permanently remove that exact directory and all of its contents. Do not
use a wildcard, a broad `models` cleanup, or a name-prefix match. Re-read the
parent directory afterward and verify the exact folder is absent. If deletion
fails or only partially completes, report the remaining paths and do not claim
success.

## Completion report

Report the result in this order:

1. `models.ini`: exact section removed, or the precise failure/partial state.
2. Related configs: each path synchronized and the removed id verified.
3. Disk: exact folder removed and post-check result.
4. Any required follow-up, such as unloading/restarting the router or repairing
   a target that was unavailable.

Never claim the model is fully removed unless all three phases have been
verified. External config files are outside this repository; only repository
changes such as `models.ini` and this skill are part of the local git diff.
