# Questrandomizer — Old-Pipeline Cleanup Plan

**Goal:** strip out the old (pre-outline) generation pipeline now that the runtime is fully
outline-native. Remove dead code, carve the old halves out of the mixed files, and bring the
docs back in line with reality.

**Status:** ✅ COMPLETE (2026-07-29). All stages 0–6 done, build green, smoke-tested (455 quests
rebuilt from outlines, no errors). Backup: `Backups\Leaves-Questrandomizer_2026-07-29_pre-cleanup.zip`.
This file can be archived. See each stage below for the executed detail.

This doc is the resumable worklist. Usage may run out mid-task across sessions, so:
- Each stage is independently buildable — **build after every stage** (`--no-incremental` if the
  server was up; otherwise stop the server first so the deploy copy lands).
- Tick the checkboxes as you go and jot findings inline. A cold session should be able to read
  this file + `HANDOFF.md` and pick up exactly where the last one stopped.
- Do NOT touch the outline schema (`Pipeline/QuestOutline.cs`) — existing profiles' cached
  outlines must keep loading. This is a code cull, not a data change.

---

## 1. Architecture reality (verified 2026-07-29)

The `HANDOFF.md` still describes the OLD path (§5 `QuestEditor`, §6 `GenerateKillQuest`, §8
`ExtractKillsFacts`/`KillsFacts`) as if current. It is stale. The actual runtime is:

```
main loop (Questrandomizer.GenerateProfileData, ~line 307):
  stored outline? → regeneration.Regenerate(quest, outline)          [NEW]
                    localeGeneration.GenerateFromOutline(...)         [NEW]
  else            → questRoller.RollQuest(clone)                     [NEW]
                    regeneration.Regenerate(quest, outline)          [NEW]
                    localeGeneration.GenerateFromOutline(...)        [NEW]

procedural (GenerateProceduralQuests, ~line 435):
  questRoller.RollWeaponRequirement()                                [NEW]
  questGeneration.AuthorKillQuestOutline / AuthorHandoverQuestOutline[NEW]
  BuildProceduralSkeleton + Regenerate + GenerateFromOutline         [NEW]

reroll:
  QuestrandomizerRerollApi → force-reroll ids → main loop RollQuest  [NEW, working]
replay:
  QuestrandomizerReplay → RerollRewards                             [BROKEN — see §5]

self-tests (QuestPipelineSelfTest, QuestRollerSelfTest):
  AuthorKillQuestOutline / BuildProceduralSkeleton / Regenerate /
  GenerateFromOutline                                                [NEW ONLY]
```

**Key de-risking finding:** the self-tests already use ONLY the new pipeline. Removing the old
path will not break them. The old code is genuinely dead, not a live test oracle.

`questEditor` is still `new`-ed at `Questrandomizer.cs:99` and stored in the field at line 69, but
**`EditQuest` is never called anywhere**. Constructed-but-unused.

---

## 2. Dead-code closure (what comes out) & carve-outs (what stays)

### 2a. Wholly-dead standalone files → DELETE ENTIRE FILE
- `src/QuestEditor.cs` — the old per-quest roller. No live caller.
- `src/RandomizeFlags.cs` — the old "what changed" record. Referenced only by dead code.
- `src/Pipeline/QuestOutlineExtractor.cs` — the old roll-then-read bridge (`FromKillsTask`,
  `FromHandoverTask`). Called only by `QuestEditor` + `AuthorKillOutline` (both dead).

Also remove in `Questrandomizer.cs`: the `questEditor` field (line ~69) and its construction
(line ~99). Check the ctor args passed only to it (e.g. `itemDB`, `tierList`) are still used by
something else before assuming they can go.

### 2b. `src/QuestGeneration.cs` — MIXED, carve out old halves
REMOVE (old, transitional — all dead once QuestEditor/OutlineExtractor go):
- `AuthorKillOutline` (both overloads, ~261 & ~277) — old scratch-roll+extract author.
- `GenerateKillQuest` (both overloads, ~154 & ~172) — builds a finished quest + flags.
- `GenerateHandoverQuest` (~500) — old finished-quest handover author. No live caller.
- `AddRewardsToQuest` (~95) — only called by the two above. **Verify** `PickRewardItem` (shared
  with `RerollRewards`) stays.

KEEP (new / live):
- `GenerateEmptyQuest` (~26) — used by `BuildProceduralSkeleton` (live).
- `BuildProceduralSkeleton`, `BuildProceduralHeader`, `AuthorKillQuestOutline`,
  `AuthorHandoverQuestOutline`, `GenerateProceduralBaseLocale`, `ExtractRewardItem`.
- `PickRewardItem`, `SetBaseQuestLocale`, `GetOrCreateQuestId`.
- ~~`RerollRewards`~~ — REMOVED in Stage 1b (its only consumer was the broken replay feature, §5).

### 2c. `src/QuestLocaleGeneration.cs` — MIXED, carve out old KillsFacts path
REMOVE (old; already superseded by `GenerateKillsLocaleFromOutline` on 2026-07-29):
- `KillsFacts` record, `ExtractKillsFacts`, `GenerateKillsLocale`, `GenerateKillsLocaleFromFacts`.

KEEP (live):
- `GenerateFromOutline`, `GenerateKillsLocaleFromOutline`, `GenerateHandoverLocaleFromOutline`,
  `GenerateHandoverItemLocale` (**still live** — called from `GenerateFromOutline` for untouched
  handover objectives), `GearFrom`, `KillTargetText`, `WeaponNameText`, `GearText`,
  `TryFactorGearGroups`, `JoinOr`, `GetLoc`, `GetWeaponCategoryLocale`, `AddLocaleToAll`,
  `AddFullLocale`, ctor/locale-loading.

### 2d. `src/QuestTools.cs` — MIXED, carve out old flag-based `Randomize*` family
REMOVE (called only by `QuestEditor` / old `GenerateKillQuest`, both dead):
- `RandomizeWeapons`, `RandomizeDistance`, `RandomizeTimeOfDay`, `RandomizeBodyPart`,
  `RandomizeTarget`, `RandomizeLocations`, `AddLocationToQuest`, `AddRandomWeaponGroup`,
  `RollMysteryArsenal`, `TrySpiceRequiredMod`, `TrySpiceForbiddenMod`, `RandomizeHandover` (~809).
- `RollTargetEntry` (~800) — **verify no live caller** first (comment says "shared with QuestEditor";
  new roller rolls targets via `QuestFacts`, so likely dead — confirm before removing).

KEEP (structural helpers used by the new roller / regeneration):
- `AddWeaponGroup`, `AddKillObjectiveToQuest`, `AddHandOverObjectiveToQuest`, `AddRewardItem`,
  `AddRewardExperience`, `AddPrerequisiteQuest`, `ChangeXPOnQuest`, `GetQuestDepth`, and anything
  `Pipeline/QuestRoller.cs` / `QuestRegeneration.cs` / `QuestFacts.cs` call. Confirmed live deps:
  `settings.Mountability.CanMountAny`, `ResolvedWeaponCategory`, `GetQuestDepth`.

---

## 3. Staged worklist (build after each stage)

> **ORDERING CORRECTION (2026-07-29, during execution):** §2a as written is NOT independently
> buildable. `RandomizeFlags` is still referenced by the old methods in `QuestGeneration` /
> `QuestTools` / `QuestLocaleGeneration` (removed in Stages 2–4), and `QuestOutlineExtractor` is still
> called by `QuestGeneration.AuthorKillOutline` (removed in Stage 2). So those two file-deletions are
> DEFERRED to the stage where their last caller disappears:
>   - `Pipeline/QuestOutlineExtractor.cs` → deleted in **Stage 2** (after `AuthorKillOutline` goes).
>   - `RandomizeFlags.cs` → deleted in **Stage 4** (after the last `Randomize*`/old-locale user goes).
> Stage 1 therefore removes ONLY `QuestEditor.cs` + the `questEditor` field/construction.

- [x] **Stage 0 — Baseline.** Green build confirmed. Baseline warnings = **2** (both CS8625:
      `QuestOutlineExtractor.cs:162`, `QuestOutlineConverter.cs:174`) — pre-existing, unrelated.
      The `QuestOutlineExtractor.cs` one will vanish when that file is deleted in Stage 2.
- [x] **Stage 1 — Delete standalone dead files** (§2a, adjusted). Deleted `QuestEditor.cs`; dropped
      the `questEditor` field (was line 69) + its construction (was line 99) in `Questrandomizer.cs`.
      Verified no ctor arg (`itemDB`/`tierList`/…) became orphaned — all still used by other
      constructions. `RandomizeFlags.cs` + `QuestOutlineExtractor.cs` deferred (see correction above).
      Build GREEN, still 2 warnings.
- [x] **Stage 1b — Remove the broken replay feature** (§5 removal spec). DONE, build GREEN.
      - `QuestrandomizerReplay.cs` DELETED; relocated `ShouldHideProfileFromWebUi` into
        `Questrandomizer.cs` (next to `GetActiveQuestDetails`) first, verbatim (kept its `.Any`).
      - `QuestGeneration.cs`: removed `RerollRewards`. `PickRewardItem` kept (shared w/ `AddRewardsToQuest`).
      - `QuestrandomizerRerollApi.cs`: removed `/replay` route, the already-stubbed 503 branch,
        `HandleReplay`, `ReplayRequest`, AND the `/quests/completed` route + `HandleCompletedQuests`
        (it existed only to feed the replay tab via the now-deleted `GetReplayableCompletedQuests`).
      - `reroll.html`: removed Re-play tab, `#pane-replay`, `loadCompleted`/`renderCompleted`/`replay`,
        the `#p-search`/`#p-list` listeners, and the `loadCompleted()` calls. KEPT the `[REPLAY]` name
        parsing + "re-played" history label + the h-action "replay" filter option (old history entries
        may still carry `action:"replay"` — annotated inline).
      - `Config.cs` `ReplayXPMultiplier` + workspace `config.jsonc` `Replay_XPMultiplier` removed
        (build re-deployed the trimmed config to the install).
- [x] **Stage 2 — Carve `QuestGeneration.cs`** (§2b). DONE, build GREEN (warnings 2→1: the deleted
      extractor's CS8625 is gone; the remaining one is `QuestOutlineConverter.cs:174`).
      - Removed `AddRewardsToQuest`, both `GenerateKillQuest` overloads, both `AuthorKillOutline`
        overloads, `GenerateHandoverQuest` (carved via a guarded line-range script with boundary
        asserts; scratchpad `carve.py`). `PickRewardItem` KEPT (shared w/ live procedural header).
      - `AuthorKillOutline`'s stale "the self-test needs it" comment was wrong — grep confirmed ZERO
        callers outside the file (self-tests use `AuthorKillQuestOutline`, which stays).
      - Then DELETED `Pipeline/QuestOutlineExtractor.cs` (last caller was `AuthorKillOutline`).
      - Reworded the `QuestOutlineScaffold.cs` naming comment that referenced the deleted extractor.
      - `RandomizeFlags.cs` STILL PRESENT (used by `QuestTools`/`QuestLocaleGeneration` old methods,
        removed in Stages 3–4) — deleted in Stage 4 per the ordering correction.
- [x] **Stage 3 — Carve `QuestLocaleGeneration.cs`** (§2c). DONE, build GREEN (1 warning, unchanged).
      Removed `KillsFacts` record, `ExtractKillsFacts`, `GenerateKillsLocale`,
      `GenerateKillsLocaleFromFacts` (two regions carved via guarded script `carve3.py`). Confirmed
      the shared sentence helpers (`KillTargetText`/`WeaponNameText`/`GearText`/`JoinOr`/`GetLoc`) and
      `GenerateKillsLocaleFromOutline` stay — they're the live outline path.
- [x] **Stage 4 — Carve `QuestTools.cs`** (§2d). DONE, build GREEN (1 warning, unchanged).
      Removed `AddLocationToQuest`, `AddRandomWeaponGroup`, `RollMysteryArsenal`, `TrySpiceRequiredMod`,
      `TrySpiceForbiddenMod`, `RandomizeWeapons`, `RandomizeDistance`, `RandomizeTimeOfDay`,
      `RandomizeBodyPart`, `RandomizeTarget`, `RollTargetEntry`, `RandomizeHandover`,
      `RandomizeLocations` (3 regions via guarded script `carve4.py`). KEPT `AddWeaponGroup`,
      `AddKillObjectiveToQuest`, `AddGearToQuest`, `AddHandOverObjectiveToQuest`, `GetQuestLocationText`,
      the reward/prereq/XP/depth helpers.
      - **Open-question #1 RESOLVED:** `QuestTools.RollTargetEntry` had NO live caller — the roller uses
        its OWN private `QuestRoller.RollTargetEntry`. Removed the QuestTools one.
      - Then DELETED `RandomizeFlags.cs` (last code users gone; only architecture-lineage comments in
        `QuestFacts`/`QuestRoller`/`QuestLocaleGeneration` still name it — kept, they're meaningful).
      - Fixed two stale inline comments that pointed at deleted methods (`QuestLocaleGeneration` daytime
        note; `QuestGeneration` "replace … below" reworded to past tense).
- [ ] **Stage 5 — Orphan sweep.** After the above, re-grep for now-unused symbols the compiler
      won't flag (unused private methods don't error in C#). Candidates:
      - **Mystery-arsenal machinery** — `KillsOutline.PinnedWeapons` comment says the mystery-arsenal
        producer is retired; `QuestRoller` has NO mystery-arsenal usage. So `Settings.BuildMysteryPool`,
        the mystery pool/blacklist fields, and related config/data (`mysteryArsenalBlacklist`,
        `chanceForMysteryArsenal`) are likely dead. **Be conservative with config/data files**
        (removing a `config.jsonc`/`assets/data` key the loader still expects can throw) — flag,
        confirm the loader tolerates absence, then remove. When in doubt leave the key and just
        remove the dead C# that reads it.
      - Any `Data/*.cs` records only the old path referenced.
      Build.
      DONE, build GREEN (1 warning, unchanged). Removed:
      - **Mystery-arsenal machinery** (confirmed dead — roller never reads the pools; only consumer was
        `BuildMountability`'s Concat): `Settings.BuildMysteryPool`, `MysteryArsenalPool`/`MysteryPistolPool`/
        `MysteryShotgunPool` + the 3 pistol/revolver/shotgun class-id consts; the `BuildMysteryPool` call
        in `Questrandomizer.cs`; `BuildMountability` no longer Concats the pool; `Config.cs`
        `ChanceForMysteryArsenal`/`MysteryArsenalWeaponCount`/`MysteryArsenalBlacklist`; workspace
        `config.jsonc` keys `chanceForMysteryArsenal`/`mysteryArsenalWeaponCount`/`mysteryArsenalBlacklist`.
      - **`QuestTools.AddGearToQuest`** — orphan (regeneration builds gear itself; only comment mentions
        remained; reworded those in `QuestRegeneration.cs`).
      - `GetQuestLocationText` was checked and KEPT (live: `QuestRegeneration.cs:113`).
- [x] **Stage 6 — Docs & memory.** DONE. Rewrote `HANDOFF.md`: header note, §1, §3, §4 diagram, §5
      (outline pipeline), §6 (outline authoring), §7.4 (spice in the roller), §7.5 (mystery removed),
      §8 (render-from-outline), §10 inventory (dropped `QuestEditor`/`RandomizeFlags`, added the
      `Pipeline/*` files + `QuestrandomizerRerollApi`), §11 (body-part rework re-mapped to the outline).
      Updated auto-memory `project_questrandomizer_cleanup` (marked DONE). This file's boxes all ticked.

---

## 4. Open questions to resolve DURING execution (don't assume)
- `RollTargetEntry` (QuestTools ~800): any live caller? (grep showed none besides the dead path —
  reconfirm after Stage 1.)
- `AddRewardsToQuest`: fully dead after Stages 1b + 2? (`PickRewardItem` must survive — it's still
  used by `BuildProceduralHeader`.)
- Mystery-arsenal pool + config/data (Stage 5): does the settings loader tolerate the keys being
  gone, or must the keys stay while only the C# reader is removed?
- Any ctor arg to `QuestEditor` (`itemDB`, `tierList`, …) that becomes unused in
  `Questrandomizer.cs` after Stage 1 — remove only if not used elsewhere.

## 5. Replay feature — BROKEN under the outline pipeline (verified 2026-07-29)

`QuestrandomizerReplay` ("re-play a completed quest for fresh rewards") was knowingly NOT ported to
the outline switch and does not work:
- `CreateReplayQuest` writes the clone into the legacy `quests.json`/`locales.json`, then calls
  `GenerateProfileData`, expecting an "extra cached quests TryAdd pass" to surface it.
- That pass no longer exists (main loop builds vanilla + procedural from outlines only), and
  `GenerateProfileData` ends by `DeleteLegacyCacheFiles` — deleting the files replay just wrote.
- Net: the replay quest is written then orphaned/deleted in the same call; it never reaches the
  player. Both web-UI endpoints (`QuestrandomizerRerollApi` HandleReplay + replayable list) are
  still wired but non-functional.

**DECISION (user, 2026-07-29): REMOVE replay now** — folded in as Stage 1b below.

### Replay removal spec (Stage 1b)
C# (`src/`):
- `QuestrandomizerReplay.cs` — delete the replay-specific members: `CreateReplayQuest`,
  `GetReplayableCompletedQuests`, `CloneQuestWithFreshIds`, `BuildReplayLocale`, `SummarizeRewards`,
  `ReplayableQuest`, `ReplayResult`, `ReplayRewardQuestNumber`.
  ⚠️ **DO NOT delete the whole file blindly.** It also defines **`ShouldHideProfileFromWebUi`**,
  which the LIVE reroll profiles dropdown calls (`QuestrandomizerRerollApi.cs:141`). Relocate that
  one method to a surviving partial (e.g. the reroll partial / main `Questrandomizer.cs`) first, then
  the file can go.
  ✅ `ResolveObjectives` / `StripRichText` / `ObjectiveDetail` are defined in `Questrandomizer.cs`
  and used by the live reroll + active-quest lists — replay only *calls* them. Leave them.
- `QuestGeneration.cs` — remove `RerollRewards` (~136); its only consumer was replay.
  **Verify** `PickRewardItem` (shared) stays.
- `QuestrandomizerRerollApi.cs` — remove the `/replay` route (`~110`), `HandleReplay` (~443), the
  replayable-list branch feeding `GetReplayableCompletedQuests` (~437), and the `ReplayRequest`
  record (~553). Keep the history-log plumbing; just stop writing "replay" actions.

Web UI (`assets/ui/reroll.html`):
- Remove the "Re-play" tab button (~17), `#pane-replay` (~43), the `replay` history-filter option
  (~67), the `data-replay` button render (~328), the `replay()` fn (~331–), and its click handler
  (~430). The `[REPLAY]` label parsing (~168) and the "re-played" history label (~388) can stay
  (harmless) or be simplified — OLD history entries may still carry `action:"replay"`.

Config:
- `Config.cs` `ReplayXPMultiplier` (~205–210) and `config/config.jsonc` `"Replay_XPMultiplier"` (~148).
  (Removing a config key is safe here — the loader tolerates unknown/missing keys, back-compat rule.)

## 6. Guardrails (from CLAUDE.md / memory)
- Local test install only (`G:\...\SPT-4.0.13\SPT`); NEVER deploy to live `10.144.154.12`.
- House style: no `=>` bodies, no LINQ/lambdas in NEW/edited code (unroll to `foreach`; keep
  `.Contains`/`.TryGetValue`). Existing LINQ in kept code can stay.
- Don't hand-revert the user's edits to a backup without asking.
- A rebuild re-deploys `config/config.jsonc` and clobbers install-side edits — edit the workspace
  config.
