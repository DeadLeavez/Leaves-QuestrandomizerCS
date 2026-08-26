# Leaves-Questrandomizer — Handoff

Last updated: 2026-08-03. Read the project-wide `4.0 Mods\HANDOFF.md` first — the build/deploy
workflow, DI rules, locale LazyLoad rules, and concurrency house rules all apply here and are
not repeated in full.

> **2026-07-29 — the runtime is now 100% outline-native.** The old pre-outline pipeline
> (`QuestEditor`, `RandomizeFlags`, `KillsFacts`, the `QuestTools.Randomize*` family, the finished-quest
> `GenerateKillQuest`/`GenerateHandoverQuest` authors, the old mystery-arsenal roller, and the
> broken **re-play** web feature) was deleted. (Mystery arsenals were later re-added natively — see §7.)
> §3–§8 and §10 below describe the CURRENT outline pipeline;
> anything still naming those symbols is a stale reference — report it. The pipeline shape: **roll →
> outline → regenerate**, where the stored `QuestOutline` is the sole source of truth (see
> `Pipeline/QuestRoller.cs`, `Pipeline/QuestRegeneration.cs`, `Pipeline/QuestOutline.cs`).

---

## 1. What the mod does

Per-profile quest randomization. For each player profile it:

- Rewrites **kill conditions**: target (Usec/Bear/AnyPmc/Savage/Any), kill count, maps,
  time of day, distance, body parts, required weapon **category** (one, or two mod-less categories
  unioned — "an AK or an SMG"), required/forbidden weapon mods, required/forbidden gear.
- Rewrites **handover conditions**: new items (tiered by quest depth), counts, FIR flag
  (optionally rainbow-colored text), or item *categories*.
- Optionally **adds** kill/handover objectives to quests that had none.
- **Generates procedural quests** from scratch (weapon-kill chains, handover chains, sniper
  chain) hung on a configured trader.
- Rewrites all affected **quest text** in English (the mod is English-only since 2026-08-02 — see §8),
  fanned out to every game language so non-en clients never see blank objectives, and generates a
  **handbook** entry listing what's in each weapon category.

Every profile gets its *own* randomization, cached to disk, stable across server restarts.

## 2. The core architectural decision (differs completely from the TS original)

The TS 3.x version mutated the **global** quest database and locale per active profile and
swapped them back and forth. On SPT 4.0 that is impossible: the DB is shared across concurrent
requests (coop via Fika makes this worse, but even a solo client sends parallel requests).

The C# design never mutates shared data:

1. **`QuestrandomizerContext`** holds a `static AsyncLocal<MongoId?> CurrentProfileId` plus static
   `ConcurrentDictionary` maps: profile→quests and profile→(sparse English locale EDITS). The full served
   locale per language is rebuilt on demand in the patch and NOT cached (§8/§8.2). AsyncLocal means "one value per in-flight request
   chain" — each concurrent request sees only its own profile id, no locking needed for the id itself.
2. **`Questrandomizer.OnRouterBeforeAction`** hooks SPT's router "before action" event. On every
   request it reads the session id, stores it into the AsyncLocal, and (for quest-relevant
   routes) triggers generation for that profile if not already done.
3. **Two Harmony postfix patches** substitute the per-profile data into responses:
   - `Patches/GetQuestsPatch.cs` — patches `DatabaseService.GetQuests()`; replaces `__result`
     with the profile's quest dictionary when one exists.
   - `Patches/GetLocaleDbPatch.cs` — patches `LocaleService.GetLocaleDb(language)`; lazily builds and
     caches the served locale by overlaying the profile's English edits onto `__result` (§8.2).
   Both no-op when no profile is in flight. This does NOT affect quest-completion logic:
   completion checks, turn-ins, and kill counters all run *while handling a client request*,
   which always passes through the router hook first, so they see the randomized quests
   (live-verified). "No profile in flight" only means server startup (other mods' OnLoad must
   see the pristine vanilla DB — it's the base the overlays are generated from) and
   session-less requests like the first handshake. If SPT ever read quests from a background
   timer thread it would see vanilla conditions, but per-profile sets are the vanilla quests
   edited in place (same ids, same shape), so that degrades gracefully rather than breaking.

So: vanilla DB stays pristine; every profile's view is an overlay applied at response time.
Both patch classes carry beginner-oriented comments explaining Harmony mechanics (`__result`,
postfix, parameter matching by name) — keep those.

## 3. Startup flow (two stages)

**Stage 1 — `Questrandomizer.OnLoad()`** at `PostDBModLoader + 1` (vanilla DB is ready):
loads `config.jsonc` and all `assets/data/*.jsonc` via `Settings.Init` (static class), migrates
legacy weapon-category format if found (logs a warning), enables the two Harmony patches,
subscribes `OnRouterBeforeAction`, loads quest whitelists.

**Stage 2 — `Questrandomizer.LateOnLoad()`** at `PostSptModLoader + 2`, via the tiny
`QuestrandomizerLateSetup` class (a single `[Injectable]` can't register two OnLoad stages).
This stage exists because it must see items **other mods** added:
- `ApplyCategoryAdditions` — folds in weapon ids other mods registered via the compat files.
- `FinalizeCategories` — prunes weapon ids whose mod isn't installed, drops emptied categories.
- `QuestHandbookGeneration.Generate` — handbook pages listing category contents.
- Compat API wiring (weapon equivalents from ShinyAirdropGuns, see §9).

## 4. Runtime flow: how a profile gets its quests

```
client request → OnRouterBeforeAction
  ├─ CurrentProfileId (AsyncLocal) = session id
  └─ EnsureProfileDataGenerated(profileId)
       ├─ fast path: already in QuestrandomizerContext? return (lock-free)
       └─ gate = generationGates.GetOrAdd(profileId, new object())
          lock(gate):
            double-check, then GenerateProfileData(profileId)
              ├─ LoadCachedProfileData: assets/generated/<profileId>/quests.json + locale.json
              ├─ LocaleWorkspace.SeedFrom(saved locale edits)   ← keeps cached quests' text
              ├─ for each vanilla quest (fresh clone of pristine vanilla):    ← §5
              │    stored outline? regeneration.Regenerate(quest, outline)
              │    else            questRoller.RollQuest(clone) → outline, then Regenerate
              │    then            localeGeneration.GenerateFromOutline(outline, quest, workspace)
              ├─ GenerateProceduralQuests(...)                  ← §6, only missing ones
              ├─ LoadWeaponEquivalents(...)                     ← §9 compat injection
              ├─ SaveCachedProfileData (guarded by generatedIdsGate for the shared id file)
              └─ QuestrandomizerContext.SetQuestsForProfile / SetLocaleEditsForProfile
                   (stores the sparse English edit set; per-language locale built lazily in the patch)
...later in the same request...
DatabaseService.GetQuests() → GetQuestsPatch swaps in profile quests
LocaleService.GetLocaleDb() → GetLocaleDbPatch swaps in profile locale
```

Concurrency notes (all live-verified after a real corruption incident): `generationGates` is a
static `ConcurrentDictionary<MongoId, object>`; the context maps are `ConcurrentDictionary`;
`generatedIdsGate` (instance object) guards the cross-profile generatedQuestIds file. Do not
weaken any of these.

`config.jsonc profileTranslator` maps a profile id to another profile id (share quests) or the
literal string `"original"` (opt out of randomization) — that's why it's a string dictionary,
not `Dictionary<MongoId, MongoId>`.

## 5. Quest editing flow — the outline pipeline (`Pipeline/`)

A quest is edited in three separated stages; the `QuestOutline` (the stored bundle of decisions) is
the sole source of truth and the ONLY thing RNG ever produces. Rebuilding is deterministic.

1. **ROLL** — `QuestRoller.RollQuest(clone)` (`Pipeline/QuestRoller.cs`). Reads a fresh vanilla clone
   and returns a `QuestOutline` of DECISIONS only — no finished quest. Skips blacklisted quests /
   respects whitelist mode; each `Decide*` rolls one facet (target, kill count, maps, time, distance,
   body part, weapon category, gear) and may decide to *add* a kill objective
   (`addKillObjectiveToQuestChance`). It reads facts back through `QuestFacts` (the outline being built
   IS the "memory" — no separate flags record). RNG happens ONLY here; every condition/link id it needs
   is minted and PINNED in the outline so a later rebuild reuses it (progress + locale keys stay put).
2. **REGENERATE** — `QuestRegeneration.Regenerate(quest, outline)` (`Pipeline/QuestRegeneration.cs`).
   RNG-free. Lays the outline's decisions over today's fresh vanilla clone (so a config/content change
   reaches quests rolled long ago with no re-roll). Handover items are re-tiered here from the pinned
   decision; weapon categories compiled from live category membership; gear/location conditions found-or-created by
   pinned id (`FindOrCreate`).
3. **TEXT** — `QuestLocaleGeneration.GenerateFromOutline(outline, quest, workspace)` (§8). A SECOND,
   independent consumer of the same outline: writes objective text into the `LocaleWorkspace`.
   Structure and wording never have to carry each other's state. It runs strictly AFTER stage 2 and
   reads that stage's OUTPUT quest through `ObjectiveFacts` — the outline alone can't describe an
   objective, because it holds only what was decided (§8).

The runtime main loop (`Questrandomizer.GenerateProfileData`) picks stage 1+2+3 for a never-rolled quest,
or stage 2+3 alone when an outline is already stored (the "rebuild from outline" path — what a re-roll
resets, and what re-applies config changes on every boot).

### 5.1 Outline verification (`Pipeline/OutlineVerification.cs`)

Between loading the stored outlines and regenerating them, `OutlineVerification.VerifyAndFix` runs a
list of **explicit, named REPAIRS** over each outline (in place, on every boot, over the pristine
vanilla clones in `profileQuests`). It is deliberately NOT a general validator — each fixer targets one
known-bad shape, must be **idempotent** (the repaired outline is saved back, so a fixer must not match
its own output next boot), and new fixers are just added to the list in `VerifyAndFix`. This is the
place to absorb data problems the roller never produces but the legacy migration
(`QuestOutlineConverter`) can leave behind, instead of teaching regeneration/locale to tolerate them.

- **Fixer #1 — multi-item handover lists.** The roller only ever writes a handover as a single
  `ItemIds` entry or a `CategoryName`; the migration pins the cached handover's whole expanded target
  list (a vanilla OR-list, or a handover CATEGORY frozen into its members — e.g. Sanitary Standards
  Part 2's list is exactly the "Info" category). Regeneration then accepts all of them but the locale
  renders only `ItemIds[0]`, so the player sees one item and can hand over several. The fix **re-rolls
  the handover completely** through the now-public `QuestRoller.FillHandover` (blanking the payload
  first so it rolls as a first-timer against the vanilla count) → a single tier-picked item or a
  live-resolving category, both of which the rest of the pipeline renders correctly.

- **Fixer #3 — specific required gear.** The pre-categories roller (and the legacy migration) wrote
  required gear as a set of pinned piece ids (`KillsOutline.RequiredGear`). Gear is category-based now
  (`RequiredGearCategory`, a weighted group from `gearcategories.jsonc`). This fixer re-rolls any
  outline still carrying a specific-item list into a fresh gear group, clearing `RequiredGear` so it
  never re-fires. No-op when no gear groups are defined.

- **Purge — blacklisted, not-yet-started quests** (`ShouldPurgeBlacklistedUnstarted`). Not a shape
  repair: it **drops the whole outline** for a quest that has since been added to `questBlacklist`, on a
  profile that never started it. Use case: a quest added to the blacklist after release (e.g. it
  randomizes badly) should revert to vanilla for everyone who hasn't touched it yet, not stay frozen in
  their already-rolled outline. Dropping the outline sends the quest back through the roll path, where
  `RollQuest` short-circuits a blacklisted quest to an empty outline → served vanilla, stable thereafter.
  Three guards, all required: the outline actually has objectives (an empty one is vanilla already — this
  is also what keeps it idempotent, since the triggered re-roll writes an empty outline back); the quest
  is blacklisted *now*; and the player hasn't started it (`LeavesUtils.hasProfileStartedQuest` — a
  started/finished/failed quest is left strictly alone to avoid desyncing live progress). Removal happens
  after the fixer loop (can't mutate the dict mid-iteration).

- **Purge — old-outline-version, not-yet-started quests** (`ShouldPurgeOldVersionUnstarted`). Companion to
  the blacklist purge, same drop-and-re-roll mechanism, different trigger: the stored outline's `version` is
  below `QuestOutline.CurrentVersion`. **`QuestOutline.CurrentVersion`** (a `double`, in `Pipeline/QuestOutline.cs`)
  is the ONE public knob — raise it (e.g. `1.1` → `1.2`) when a mod update should re-roll locked quests so
  they pick up new content (new weapon categories, changed odds). Every `new QuestOutline` stamps the current
  version via the field default, so a fresh roll is always current and this never re-fires on it. Guards
  mirror the blacklist purge — **not procedural**, **has objectives** (empty = vanilla already), version **`<`**
  current (compared with `<`/`>=` only, so the double never hits equality/representation issues), and the
  player **has not started it** (started/active quests keep their roll — progress is never disturbed). Started
  quests therefore keep a stale `version` harmlessly; nothing else reads the field.

### 5.2 One-session quests + added objectives (the `oneSessionOnly` strip)

A handful of vanilla quests are **one-session**: `AvailableForFinish` conditions carry
`oneSessionOnly=true`, meaning EVERY condition must be satisfied within a single raid (Scout, The
Bunker - Part 1, Colleagues - Part 1, Nothing Fishy About This, Pets Won't Need It - Part 1, ... —
typically "visit these places AND extract alive on map X"; ~54 in the vanilla DB). When the mod **adds**
an objective to one of these (a kill on another map, or a handover), or rewrites a kill onto a different
map, the quest can no longer be finished in one raid, so the client's one-session gate never opens:
**every objective shows ticked but no Complete button appears**, and the one-session counters reset to 0
at each raid end. **The same stuck state also strikes quests the mod never touched** — a vanilla
one-session quest that desynced on its own (all conditions met in a raid → written to
`CompletedConditions` → counters wiped at raid-end before turn-in), which happens on live, especially
under Fika coop (worked example: `Colleagues - Part 1` `5edab736cc183c769d778bc2`, 2026-08-12).

Empirical facts that pin the diagnosis (2026-08-04, live-verified on a real player profile):
- The in-game objective **checkmarks** render from `PmcData.Quests[].CompletedConditions` (which
  accumulates across raids, so they show green), but **turn-in readiness is gated on the LIVE
  `TaskConditionCounters` value**, not `CompletedConditions`. A one-session counter that reset to 0
  therefore blocks hand-in even though its id is in `CompletedConditions`. (This is the same
  counter-is-authoritative rule the web UI already relies on — see §8/`ResolveObjectives`.)
- So the web UI ALSO shows these objectives incomplete (it reads the same reset counters). Same root
  cause, not a second bug.

**Fix, two halves**:
1. **Prevent it going forward on modified quests** — `QuestRegeneration.Regenerate` clears
   `OneSessionOnly` on every `AvailableForFinish` condition of any quest it modified
   (`outline.Objectives.Count > 0`). Untouched quests keep their vanilla one-session behaviour at
   regeneration time. Deterministic transform, reapplied every boot over a fresh vanilla clone (same
   class as the XP multiplier), so it never compounds.
2. **Detect + heal any wounded quest** — `OneSessionQuestRepair` (see §10), run at the end of
   `GenerateProfileData` AND on demand from the web UI's **Verify quests** button
   (`Questrandomizer.VerifyAndRepairProfile` → `POST /questrandomizer/api/verify`). It is **not** gated
   on "did we modify it": for EVERY accepted quest it looks for the wound signature — a `CounterCreator`
   condition whose id sits in `CompletedConditions` but whose live counter is below target — which cannot
   arise in healthy play (a normal counter only climbs and persists). When a quest is wounded it (a)
   restores each stuck counter to target (only ever raising one the game already flagged complete, so it
   never invents progress) and (b) strips `OneSessionOnly` off that quest's *served* conditions so a
   later raid-end can't reset them and re-stick it. Healthy one-session quests never match, so their
   "do it in one raid" behaviour stays fully intact — this is the surgical alternative to a blanket
   flag-strip. Self-dormant once healed (the signature no longer matches), and re-heals every boot if the
   change wasn't persisted. **It does NOT save the profile itself** — the heal is a live mutation of the
   profile SPT already holds, left for the server to persist on its own schedule (raid end, logout,
   autosave, shutdown); we deliberately don't reach into SPT's save lifecycle. **Note:** a profile SPT
   flags invalid (e.g. missing modded items in a test install) heals in memory but the server won't write
   it back — a test-env artifact, not a bug (boot re-heals regardless).

## 6. Procedural generation (`QuestGeneration.cs`)

Procedural quests (weapon-kill chains, handover chains, sniper chain, hung on `QuestGen_Trader`) are
authored as OUTLINES too, the same shape as edited-vanilla ones: `AuthorKillQuestOutline` /
`AuthorHandoverQuestOutline` write a `QuestOutline` directly (rolling the reward item here, and for kill
quests the weapon requirement via `questRoller.RollWeaponRequirement`), minting + pinning every id the
player's progress hangs off (objective condition ids, prerequisite link id). The RNG-free rebuild is
`BuildProceduralSkeleton` (`GenerateEmptyQuest` + pinned XP/reward/prereq from the outline's
`ProceduralHeader`) → `QuestRegeneration.Regenerate` → the locale passes — identical to a stored outline
on any later boot. Reward items come from `PickRewardItem` (tiered by `QuestGen_RewardPools`); counts/FIR
from the `QuestGen_*` config block. `GetOrCreateQuestId` keeps ids stable across regenerations by
persisting name→id in a shared file (this is what `generatedIdsGate` protects).
`QuestGen_TotalRunnerQuests`/`TotalPostmanQuests` are parsed but **not implemented** — the TS
original said the same; don't delete the config keys.

## 7. The weapon system (most complex subsystem, lives in `Settings.cs`, `ResolvedWeaponCategory.cs`, `WeaponMountability.cs`, `QuestTools.cs`)

### 7.1 Categories
`assets/data/weaponcategories.jsonc` defines named categories (weights, hand-listed weapon ids,
requiredMods/forbiddenMods as flat mod-category name lists, and optional **dynamic selectors**).
At LateOnLoad, `CompileCategory` builds a `ResolvedWeaponCategory` (compiled required/forbidden mod
shapes + the raw selector inputs), then `ResolveDynamicWeapons` expands the selectors against the
**live item DB** (so other mods' guns join automatically). A **legacy** (TS-era nested) mod format
is auto-migrated with a logged warning (`MigrateLegacyModFormat`).

**Two selector kinds, and how they combine** (`ResolveDynamicWeapons`):
- `calibers` (human-written, e.g. `"9x19"`, matched tolerantly against `Properties.AmmoCaliber` —
  lowercase + strip dots + strip `caliber` prefix, then **prefix**-match, so `"9x18"` covers both
  `Caliber9x18PM` and `Caliber9x18PMM`) **build the CANDIDATE pool**. With no `calibers`, the
  candidate pool is *every* weapon.
- `fireRate` (`{ "above"/"below": <strict rpm bound> }`, a `FireRateBound`) **NARROWS** the pool:
  a candidate must have `Properties.BFirerate` in range (a gun with no fire rate — bolt-action, most
  shotguns — is excluded). Calibers and fire rate **intersect**; there is no OR/expand mode.
- `excludeWeapons` prunes matches; the result merges into the hand-listed `weapons`.
- Two `HasSelectors` helpers (on `WeaponCategoryEntry` and `ResolvedWeaponCategory`) are the single
  source for "has any selector kind". Distinct from the resolve loop's `hasCandidateSelector`
  (`calibers` only), which is what lets a **fire-rate-only** category default to "all weapons then
  narrow" instead of matching nothing.
- A category with **no weapons and no selectors** is the deliberate "ANY weapon" shape
  (`ResolvedWeaponCategory.AnyWeapon`), e.g. `Suppressed` = empty + a required silencer.

> **Selectors were pared down (2026-08-09).** An unused `baseClasses` (item-class parent-chain)
> selector and a reflection-driven generic `statSelectors` (any numeric Properties field by name)
> were removed in favour of the single concrete `fireRate` bound — fire rate was the only stat that
> made sense as a category, so the reflection machinery (`GetWeaponStat`/property-name lookup/typo
> warnings) was pure cost. `IsUnderClass` stays (still used for the weapon-vs-stationary gate).

Selectors resolve to explicit weapon id lists (NOT to equivalents — a shiny/modded gun that matches
a selector joins as a real member in its own right; see §9 for how equivalence relates to categories).

**Never use the quest `weaponCaliber` field** — the client ignores it (proven live). Caliber
selectors therefore resolve to explicit weapon lists at load time instead.

### 7.2 Required mods = OR of ANDs
The client counts a kill if the weapon matches **any** required-mod group, where a group
matches only if **all** its mods are present. So "scope AND suppressor, from these category
lists" is expressed as the **cartesian product** of the category lists, one group per
combination. `QuestTools.AddWeaponGroup` copies compiled shapes onto the condition.

> **Roll-time mod "spice" was REMOVED (2026-07-29)** — the whole feature (a per-quest chance for a
> weapon-category quest to gain one extra required/forbidden mod category) is gone, along with its
> `spiceModCategories` / `chanceToSpice*` config keys, `QuestRoller.RollSpice`,
> `QuestRegeneration.ApplySpice`, the `spicedRequiredMod`/`spicedForbiddenMod` outline fields, and
> the entire `WeaponMountability` BFS table that existed only to validate spice.

> **Mystery arsenals were RE-ADDED (2026-08-06)** — the 2026-07-29 removal note below is superseded.
> `QuestRoller.RollMysteryArsenal` is back: on `chanceForMysteryArsenal`, a weapon requirement becomes a
> fixed set of specific weapons instead of a category — one guaranteed pistol + one guaranteed shotgun
> (drawn from the `Pistol`/`Shotgun` categories in `weaponcategories.jsonc`) + `mysteryArsenalWeaponCount`
> random weapons from the union of all rollable categories. It writes the outline's `pinnedWeapons` set
> (which regeneration already applies verbatim, no late-binding) and sets the `mysteryArsenal` flag.
> Falls back to a normal category roll if the Pistol/Shotgun categories are missing.

### 7.5 Targets & kill counts
The roller picks a target from `misc.jsonc validTargets` (ordered hardest→easiest); kill count is
scaled by how far the target moved along that ordering (`killCountWhenTargetTypeChangesFactor`)
plus a random factor. Data facts: `savageRole` is camelCase & scav-only; bare `Savage` = all
non-PMCs incl. bosses. An "enemy categories" system (Tourists/Locals) was built, verified, and
**removed on purpose** — don't re-add.

## 8. Locale generation (`QuestLocaleGeneration.cs`) — ENGLISH-ONLY

> **2026-08-02 — the 16-language translation pipeline was ripped out.** The mod is English-only now.
> Nobody translated it on Crowdin, and no large SPT mod ships non-en locales. Gone: the 16 non-en
> `localization/<lang>/` folders, `weaponCategories.json`/`gearCategories.json` locale files,
> `crowdin.yml`, the per-language `GetLoc(key, lang)` fallback chain, and `GetWeaponCategoryLocale`/
> `GetGearCategoryLocale`. See §8.2 for how non-en clients still get non-blank text.

Text is generated straight from the stored outline as an independent second pass over the decisions
(not interleaved with the roll):

1. `GenerateFromOutline(outline, quest, workspace)` walks the outline's objectives and dispatches:
   `GenerateKillsLocaleFromOutline` and the handover locale builders.

> **2026-08-10 — the text pass reads `ObjectiveFacts`, not raw outline fields.** It used to render
> `KillsOutline`/`HandoverOutline` fields directly, which is wrong because **the outline is sparse**:
> it stores DECISIONS, so an objective whose map was rerolled has `maps` and nothing else — while its
> vanilla time window, body parts, boss target, count and weapons are all still live requirements on
> the built quest, and every one of them went unsaid. A boss quest with a rolled weapon category read
> `Kill 0 using [AKs] at [Any map]` (the roller never decides `savageRoles`, and `DecideCount` returns
> early for special targets, so both were null). Now every mechanical value goes through
> `ObjectiveFacts.GetX(quest, objective)` — "the decision if there was one, else what the quest says".
> The outline is still consulted directly for the three things that exist nowhere else: weapon category
> NAMES, the gear group LABEL, the handover category NAME (the built quest has those expanded to id
> lists). `quest` here is the **regenerated** quest, not pristine vanilla, and that is load-bearing —
> see `ObjectiveFacts.cs`'s two-callers note.
   (`quest` is needed for exactly one thing: FORBIDDEN gear, which the mod never rolls — it's vanilla's
   own — and so isn't in the outline.)
2. The English sentence is built **once** (`List<string> parts`, joined with spaces). Connective words
   ("Kill", "using", "at", "wearing", "Hand over", …) are hard-coded string literals in the builders.
   Only DATA-keyed names — map ids, enemy roles, body parts, `ITEMCATEGORY_*` — are looked up, from the
   single English table `assets/data/localization/en/base.json` via `GetLoc(key)` (falls back to the raw
   key). Item/trader names come from `getLocale("en", …)`. A weapon category's display name IS its name;
   a gear group's label is `settings.GearCategories[group].DisplayName` (folded into `gearcategories.jsonc`).
3. That one English string is written to the **flat** `LocaleWorkspace` (`id -> text`, no language
   dimension). Generation stores it as one sparse per-profile edit set
   (`QuestrandomizerContext.SetLocaleEditsForProfile`); the served locale for any given language is built
   **lazily** by `GetLocaleDbPatch` on first request — see §8.2.

Rendering details worth knowing: `TryFactorGearGroups` detects cartesian-product gear (e.g.
Punisher part 5) and renders `[A or B] + [C or D]` instead of exploding all combinations, caps
at 6 groups with "(+N more)"; `JoinOr` gives " or " separators; an objective with no Location
condition renders `[Any map]`. A quest with the gear flag set but an **empty** gear list logs a HUGE
warning (that combination silently rendered nothing in the TS version and hid real bugs). The shared
sentence helpers (`KillTargetText`/`WeaponNameText`/`GearText`/`GetLoc`) are the single place objective
wording is decided.

### 8.2 Why non-English clients still get non-blank text (the fan-out)

The mod's text is English, but a client can request `/client/locale/fr`. `LocaleService.GetLocaleDb(fr)`
returns the FR dict **verbatim** — it does NOT per-key-fall-back to en (the only fallback is whole-locale,
`en-us`→`en`). So a per-profile condition id that exists only in `en` would render **blank** in FR. That
is the one outcome to avoid.

Fix (build on demand, keep nothing): generation stores ONE sparse English edit-set per profile
(`QuestrandomizerContext.SetLocaleEditsForProfile`; `LocaleWorkspace` is a flat `id -> text` map).
`GetLocaleDbPatch` builds the served locale for whatever language a client requests **fresh on every call**:
it copies `__result` (that language's vanilla locale, which SPT already handed the postfix — a
`new Dictionary(src)` is a full logical clone since string keys/values are immutable), overlays the English
edits, and returns it. The merged dictionary is **not retained** — it's a ~5MB clone of SPT's own locale and
pure derived data (vanilla + our sparse edits, both already on hand), so holding one resident per
`(profile, language)` was the mod's biggest memory cost, spent to save a rebuild on a path the game client
hits exactly ONCE per startup. Only the tiny per-profile edit set (a few KB) stays resident; the big clone is
transient and collected after the response serializes. (2026-08-02 — dropped the old cross-request
`builtLocaleByProfile` cache. If a profiler ever shows `GetLocaleDb` is hot WITHIN one request, memoize
per-request in an `AsyncLocal`, not a cross-request map.) This still guarantees no blank key — the same
outcome SPT's own `CustomItemService.AddToLocaleDbs`
achieves for custom items (verified by decompile). Net effect for a French client: menus and item names stay
French, vanilla (untouched) objectives stay French, and only the mod's **randomized** kill/handover
objectives are English. Live-verified across en/fr/ru/jp: 0 missing, 0 blank; rewritten objectives
byte-identical across languages, vanilla ones localized. (Repeat with `scratchpad/verify2.py` if suspect.)

`QuestHandbookGeneration.cs` writes handbook pages listing each weapon category's contents so
players can see what counts. Locale-related trap that already bit us once: it must resolve all
item names **before** registering its locale `AddTransformer` (recursive-lock crash otherwise —
see project handoff §4.3).

### ⚠️ 8.1 Per-profile locale vs third-party HTTP response caches (2026-07-17)

**Symptom:** in coop, generated quests show their NAME but blank objectives, and only *some*
edited quests are affected. Single-player looks perfectly fine.

**Cause:** this mod makes `/client/locale/<lang>` **per-profile** (§2 / `GetLocaleDbPatch`). In
vanilla SPT that endpoint is static-after-load — the same for every player — so a perf mod
caching it by URL is doing something entirely reasonable. `CompoundingPerf`'s `ResponseCache` has
a hardcoded path list that includes `/client/locale/en` and `/client/menu/locale/en`. The first
player to request it after boot populates the cache; **every other player is then served that
player's locale.**

Why the symptom looks so weird — `/client/quest/list` is *not* on that cached list, so each
player still gets their own quests, just someone else's text:
- generated quest **names** survive (their ids come from the shared `ids.jsonc`, so the key
  exists in every profile's locale);
- their **conditions** go blank (condition ids are minted per-profile — your ids aren't in the
  other player's dictionary);
- "only some" edited quests break — cache-reused quests keep their ids and render fine, only
  freshly-rolled conditions blank out.
- English-only, because the cached path is the literal string `/client/locale/en`.

**Fix:** `Server.ResponseCache.Enabled: false` in `user/mods/CompoundingPerf/config.json`. Its
`AdditionalPaths` is additive-only, so a path cannot be un-cached from config.

**How it was proven** (repeat this if a similar bleed is ever suspected — it's decisive and takes
a minute): request `/client/locale/en` from the running server once per profile with
`curl -sk --cookie "PHPSESSID=<profileId>"`, then compare. The responses came back byte-identical
across six profiles and matched exactly one profile's `assets/generated/<id>/locales.json` at
100% while every other profile matched 24–31% (incidental overlap). Disabling the cache made each
profile's response match its own cache with 0 missing / 0 stale. Note the server is HTTPS with a
self-signed cert (`-k`), and responses are zlib — `zlib.decompress(...)` then read `["data"]`.

**The general rule:** any mod that turns a normally-static SPT endpoint into a per-profile one is
on a collision course with every response cache in the load order, and the collision is invisible
until two clients connect. If a future SPT/mod combination caches `/client/quest/list` too, the
same bleed hits quests themselves — which would be much worse than text.

## 9. Compat API (`QuestrandomizerCompatibility.cs`)

Public surface other mods call to register **weapon equivalents** ("shiny RSH-12 counts as
RSH-12"). ShinyAirdropGuns calls it (soft dependency, see project handoff §6). Pairs from here plus
`config/weaponCompatibility/*.jsonc` are flattened once into a transitive `original → [everything
that counts as it]` map (`BuildTransitiveWeaponEquivalents`), shared by every consumer.
Registration must happen before profiles generate — ShinyAirdropGuns registers during its own
OnLoad, which precedes any client request.

**Equivalence does NOT apply to purely selector-based categories** (the 2026-08-09 rework). The rule
lives in one place — `ResolvedWeaponCategory.SkipsEquivalence` (= has selectors AND no hand-listed
weapons) — and every consumer reads it:
- **Counting** (`LoadWeaponEquivalents`): injects a weapon's equivalents into a profile's quest
  kill-conditions, EXCEPT conditions whose objective is a group of purely-selector categories.
  `BuildGroupConditionIds` reads this profile's outlines and collects the `KillsConditionId` of every
  objective all of whose `weaponCategories` are `SkipsEquivalence`; those are skipped. **Pinned** and
  **vanilla** weapon requirements are never in that set — a shiny of a specifically-named gun SHOULD
  count, exactly like a vanilla "kill with M4A1".
- **Display** (in-game category sheet, web-UI category view, the "Part of category" weapon-description
  stamp via `AddCategoriesToWeapon`) all list/stamp equivalents only when `!SkipsEquivalence`.
- **Why**: a selector category is a live DB query, so a shiny/modded member is already a direct match;
  equivalence would only drag in guns the query excludes (a 9x19 platform-twin of a 5.56 member leaking
  into "5.56"). A hand-listed or **mixed** category keeps equivalence — a shiny of a hand-picked gun
  isn't a direct member, so the counts-as link is the only thing that makes it count.

> The in-game sheet (`QuestHandbookGeneration`) and the web-UI category view (`Questrandomizer.WebUi`)
> run the same gate+member-dedup on equivalents in parallel (different output shapes). If the display
> rule changes, edit BOTH. (`BuildAllWeaponsList` is intentionally ungated — a global "what counts as
> what" view.)

## 10. File inventory

### src/
| File | Role |
|---|---|
_(The `QR` facade was removed 2026-08-10. Shared LeavesUtils is now reached as **`Leaves.Utils.Instance`** — a self-registered static handle on the library class itself, see the LeavesUtil HANDOFF. Every helper here reads `Leaves.Utils.Instance` directly; nothing threads a `leavesUtils` parameter, and there is no per-mod init to call.)_
| `Questrandomizer.cs` | Core of the `partial class` (split 2026-08-03): OnLoad/LateOnLoad, router hook, per-profile generation pipeline, weapon-equivalents + modded-quest marking (§3, §4). |
| `Questrandomizer.WebUi.cs` | Same partial class — the **web-UI data layer**: query views (rerollable/active/browse/handbook/debug), the re-roll actions, web DTO records, and their exclusive helpers. Read by `QuestrandomizerRerollApi`. Feature layer: calls INTO core/cache, never called back. |
| `Questrandomizer.Cache.cs` | Same partial class — the **persistence/migration layer**: outline load/save, legacy-cache migration, pristine vanilla-quest-id read, `IsEngagedStatus`. Bottom layer: depends on nothing else in the class; core + web call DOWN into it. |
| `QuestrandomizerLateSetup.cs` | Tiny second-stage OnLoad shim (§3). |
| `OneSessionQuestRepair.cs` | **One-session desync healer** (§5.2): for any accepted quest showing the wound (a `CounterCreator` id in `CompletedConditions` but its live counter below target), restores the counter to target and strips `OneSessionOnly` off the served copy so it can't re-stick. Not gated on "did we modify it" — heals ours and vanilla alike, but only quests already broken. Run in `GenerateProfileData` and on demand via `VerifyAndRepairProfile` (web UI **Verify quests** button → `POST /api/verify`). Self-dormant once healed. |
| `QuestrandomizerContext.cs` | Static AsyncLocal + ConcurrentDictionary bridge between router hook and patches (§2). |
| **`Settings.cs`** | **Static class** `Settings` (renamed from `QuestrandomizerSettings` 2026-08-10): loads all data files (`Settings.Init` in OnLoad), compiles weapon categories (Apply/Compile/Resolve/Finalize in LateOnLoad). Read everywhere as `Settings.Config`/`Settings.Misc`/`Settings.ResolvedCategories`/... — no `settings` parameter is threaded any more. Two-phase, mutated ONLY during LateOnLoad, read-only after (safe for concurrent per-profile generation). |
| `QuestrandomizerRerollApi.cs` | Web-UI HTTP endpoints: profiles, active/search quest lists, re-roll, history (`/questrandomizer/ui`). Browser-facing → NO game session, so every endpoint takes an **explicit `?profileId=`** query param. **Dev debug lookups** (server running, HTTPS `127.0.0.1:6969`, curl `-k`): `GET /questrandomizer/api/profiles` lists valid `{profileId, nickname, level}`; `GET /questrandomizer/api/debug/quests?profileId=<PID>` → regenerated per-profile quests as JSONC; `GET /questrandomizer/api/debug/locale?profileId=<PID>` → that profile's locale edits; `GET /questrandomizer/api/debug/outlines?profileId=<PID>` → the per-profile **outlines** (source-of-truth decision objects, not regenerated quests) as JSONC. Missing param → `404 {"error":"Missing profileId."}`. |
| `QuestrandomizerCompatibility.cs` | Weapon-equivalents API for other mods (§9). |
| `Patches/GetQuestsPatch.cs` | Harmony postfix: per-profile quest swap (§2). Heavily commented for beginners. |
| `Patches/GetLocaleDbPatch.cs` | Harmony postfix: per-profile locale swap (§2). |
| **`Pipeline/QuestOutline.cs`** | The outline schema — the stored bundle of roll decisions. No outline has ever been live-deployed, so its shape **can** change freely (only local test profiles hold outlines, and unknown keys are ignored on load — a removed field just stops applying). The separate legacy `quests.json` cache migration is what still needs care. |
| **`Pipeline/QuestRoller.cs`** | STAGE 1 (ROLL): reads a vanilla clone → returns a `QuestOutline` of decisions; all RNG lives here, incl. weapon rolls (§5). |
| **`Pipeline/QuestRegeneration.cs`** | STAGE 2 (REGENERATE): RNG-free rebuild of a real quest from an outline over fresh vanilla (§5). |
| **`Pipeline/OutlineVerification.cs`** | Pre-regeneration repair pass: an expandable list of explicit, idempotent fixers for known-bad outline shapes (§5.1). |
| **`Pipeline/ObjectiveFacts.cs`** | The "what does this objective ACTUALLY require" layer: pure statics over (quest, outline), decision-first with a quest fallback. Two callers, two source quests — the roller passes VANILLA, the locale pass passes the REGENERATED quest (§8). Kept as its own class on purpose (clean split), never re-merged into QuestFacts. |
| **`Pipeline/QuestFacts.cs`** | **Static class** (`QuestFacts.Init(itemDB)`): roller-facing facts — config/quest-graph (difficulty, blacklist, depth, add-gates via static `Settings`/`QuestTools`) plus thin forwards to `ObjectiveFacts` with the vanilla quest. Called `QuestFacts.X`, no `facts` parameter threaded. |
| **`Pipeline/QuestOutlineScaffold.cs`** | Pre-roll STRUCTURE read out of the pristine vanilla quest (a shell per touchable objective). |
| **`Pipeline/QuestOutlineConverter.cs`** | Migrates legacy cached data into outlines on load. |
| **`QuestTools.cs`** | **Static class** (`QuestTools.Init(modPath, itemDB, quests)`): structural quest-building helpers used by roll/regeneration: `Add*Objective`, `AddWeaponGroup`, reward/prereq/XP, quest-depth (§7). Called `QuestTools.X`. (`QuestDepth` beside it stays a plain data class.) |
| `QuestGeneration.cs` | Procedural quest OUTLINE authoring + RNG-free skeleton build (§6). |
| **`QuestLocaleGeneration.cs`** | **Static class** (`QuestLocaleGeneration.Init(modPath)`): English objective text rendered once from the outline via `ObjectiveFacts`, fanned to all languages (§8, §8.2). Called `QuestLocaleGeneration.X`. |
| `QuestHandbookGeneration.cs` | Handbook pages for weapon categories, mod categories, AND gear groups; also stamps each weapon/mod/gear item's own description with a "{QR} Part of category: [X]" line (§8). |
| `LocaleWorkspace.cs` | Sparse per-profile locale edit buffer (see its class comment). |
| `ResolvedWeaponCategory.cs` | Compiled category: explicit ids + mod shapes (§7.1). |
| `Config.cs` | Mirrors `config.jsonc` field-for-field; documents the non-`required` back-compat rule. |
| `ModMetadata.cs` | SPT mod metadata record. |
| `Data/*.cs` | Records mirroring the `assets/data/*.jsonc` shapes. |

### assets/data/
- `misc.jsonc` — valid maps/targets, easy-map list, location id map, body-part groups, reward
  crate pools.
- `weaponcategories.jsonc` — categories + selectors (§7.1).
- `itemtierlist.jsonc`, `handovercategories.jsonc` — handover randomization pools.
- `gearcategories.jsonc` — gear GROUPS the "wear X" objectives roll over: named groups of
  interchangeable pieces (`{ slot, weight, items[] }`), picked by weight and late-bound like weapon
  categories. Replaced the old flat `gearlist.jsonc` (still on disk, no longer loaded). Each group's
  display **label** lives on the group itself as `displayName` (folded back into this file 2026-08-02
  when translations were dropped). Surfaced in the web UI handbook (a **Gear categories** tab in
  `handbook.html`) and deep-linked from re-roll quest cards (`questrandomizer.html` `categoryHref`); the data is
  served by `GetHandbookData().GearCategories`. Also surfaced **in-game** (2026-08-03): a "Gear
  Categories" handbook folder with one book item per group listing its pieces, and a "{QR} Part of
  category: [label]" line stamped onto each gear piece's own description — mirrors the weapon system, see
  `QuestHandbookGeneration.GenerateGearCategorySheet`/`AddCategoriesToGear`.
- `questpoints.jsonc` — quest depth data for scaling.
- `localization/en/base.json` — the ONE remaining locale file (English-only, see §8): a key→name table
  for the data-keyed lookups (map ids, enemy roles, body parts, `ITEMCATEGORY_*`), the `QG_*` procedural
  quest flavor text, and the handbook sheet labels. Connective words are inlined literals, not keys here.
  Weapon-category display names are the category name; gear-group labels are `gearcategories.jsonc`'s
  `displayName`. The 16 non-en folders, the two category locale files, and `crowdin.yml` are gone.
- `generated/<profileId>/` — **runtime cache**, excluded from build copy; delete to force a
  profile's regeneration.

### config/
- `config.jsonc` — all knobs (see `Config.cs` for the annotated mirror).
- `questwhitelists/enabled/*.jsonc` — whitelist files (vanilla.jsonc shipped).
- `weaponCompatibility.jsonc` — manual weapon-equivalence entries.

## 11. Pending work

- **Body-part rework (designed, not started; workspace was rolled back to the pre-rework
  backup).** Replace `misc.jsonc bodyParts` (`List<List<string>>`) with entries of
  `{ "parts": [...], "text": "flavor phrase" }`: `["Head"]` → "with headshots",
  `["LeftLeg","RightLeg"]` → "and leg em", `["Chest","LeftArm","RightArm"]` → "and avoid the
  head". The text renders verbatim via the GetLoc raw-fallback (translatable by adding the same
  string as a key in a language's base.json), replacing the current `in: [Head or ...]`
  rendering, positioned after the location part. **NOTE (2026-07-29): this spec predates the
  outline cull — re-map it to the outline pipeline.** It would now touch: `Data/MiscData.cs` (new
  `BodyPartEntry` record), the body-part `Decide*` in `Pipeline/QuestRoller.cs` (store parts + text
  in the outline / `KillsOutline`), `Pipeline/QuestOutline.cs` (carry the text — mind the
  don't-break-cached-outlines rule), and `QuestLocaleGeneration` (render text instead of part list),
  plus `misc.jsonc`.
- **Fika multi-client test** — see project handoff §7.
