# The rolling decision tree

Extracted from `QuestEditor.EditQuest` + the `QuestTools.Randomize*` helpers, 2026-07-25. This is
the map we need to make **rolling operate on an extracted outline of a vanilla quest** rather than by
mutating a scratch `Quest` and reading the decisions back out.

---

## Order of decisions (quick reference)

The whole roll as a flat sequence — just what gets decided, in the order the roller does it. The
detailed tree below expands each with its gates, inputs, and outline output.

**Quest-level (once per quest)**
1. Add a kill objective? *(only if the quest has none)*
2. Add a handover objective? *(only if the quest has none)*

**Per kill objective** *(each CounterCreator with a Kills condition)*
3. Edit this objective at all? *(gate — if no, skip 4–11)*
4. Location — add maps, or reroll existing maps
5. Gear — add required gear, or reroll existing
6. Target — swap who you kill
7. Body part
8. Time of day
9. Distance
10. Weapon — category (or two mod-less categories unioned)
11. Kill count — adjust

**Per handover objective** *(each HandoverItem)*
12. Edit this objective at all? *(gate — if no, skip 13–15)*
13. Item — a specific tier-picked item, or a whole category
14. Found-in-raid?
15. Handover count — adjust

Two ordering constraints inside a kill objective are load-bearing (everything else is order-independent):
- **4 (Location) before 8–9 (Time/Distance)** — Factory/Labs clear the time and distance windows, so
  those steps read the maps decided in step 4 (via `QuestFacts.IsFactoryOrLab`, i.e. `EffectiveMaps`).
- **6 (Target) before 11 (Count)** — a target swap scales the kill count, so step 11 reads step 6's
  difficulty swing (via `QuestFacts.TargetDifficultyDelta`, which compares vanilla target vs outline
  target). Neither step "remembers" anything — step 6 wrote its answer into the outline, step 11 reads
  it back.

---

## Where we are vs. where this goes

**Today (dual-write bridge):** the roller takes a *clone of the vanilla quest*, mutates it in place
through `Random*` helpers, and shuttles the decisions it made into a `RandomizeFlags` accumulator;
then `QuestOutlineExtractor` reads the flags **and** the mutated condition back out into an outline.
Three moving parts — the scratch mutation, `RandomizeFlags`, and the extractor — exist only to let the
outline be produced *without restructuring the roller first*, so migration could be verified against
the live roll. That job is done (100% structural + text parity, procedural 100%).

**Target (outline-native):** rolling becomes

```
Rolling:  Roll(vanillaQuest, rolledOutlineSoFar) + config + RNG  ->  QuestOutline
```

Two sources, and nothing else — but they play different roles:
- the **outline being built** — read-**write**. It is the roll's OUTPUT, the single source of truth for
  *anything the roll decided*: each `Decide*` step **writes** its decision here, and the helpers **read**
  earlier decisions back out on demand. This is where the rolls are saved — it accumulates precisely
  because it is the deliverable.
- the **vanilla quest** — read-**only** *input*, consulted only for the few **shape facts** that are not
  decisions (§1). Never mutated; never copied into a side structure.

**THE INVARIANT (what "no fact-book" means concretely):** the outline is the *only* thing that
accumulates. No *other* field on any roller object outlives a single `Decide*` call — no side state, no
shadow copy of the decisions. If a value has to survive between decisions, that is the definition of a
decision, and decisions live in the outline (which persists them for real). There is no `RandomizeFlags`
and no `VanillaFacts` struct: those were *derived, discarded* accumulators — shadow copies that flowed
through the roll only to be read by the extractor — and both **delete**. The outline threading through
as the return value is the mechanism, not a violation: it is saved, not thrown away. Facts are
re-derived by small pure helpers each time they're asked for (§4), from the outline or the vanilla quest.

`RandomizeFlags` and `QuestOutlineExtractor` **delete entirely**; the roller writes outline fields
directly. Regeneration is unchanged (`outline + vanilla quest -> real quest`).

What used to look like "intra-roll data dependencies" needing a mutable object (§4) are just helpers
that read the *partial outline* — e.g. the distance decision asks "what maps got decided?" and reads
them back from the outline it's building. Ordering is the only requirement, and every helper falls back
to the vanilla value when its outline field is still null, so "asked before decided" is always safe.

---

## 1. Every fact, and which of the two sources answers it

No fact is materialized up front. Each is derived on demand by a helper. The **Source** column is the
whole point of this section: it says whether a given helper reads the **[outline]** (a decision), the
**[vanilla]** quest (a shape fact), or just needs the **[id]** (a config/graph lookup). Only the
`[vanilla]` rows are why the roller takes the vanilla quest as an argument at all.

### Quest-level
| Fact | Source | Reads | Used by |
|---|---|---|---|
| `isBlacklisted` | **[id]** | `QuestBlacklist.Contains(id)` | gate 0 |
| `isEasyQuest` | **[id]** | `EasierQuestList.Contains(id)` | many gates (suppress difficulty) |
| `depth`, `level` | **[id]** | precomputed quest-graph `depthList[id]` | Added-objective count scaling |
| `hasKillsFailstate` | **[vanilla]** | `Conditions.Fail` contains a `Kills` | suppresses kill edits/adds |
| `hasAnyKills` | **[vanilla]** | any `Kills` in `AvailableForFinish` | add-kill gate — outline is sparse, so untouched objectives only show here |
| `hasAnyHandover` | **[vanilla]** | any `HandoverItem` in `AvailableForFinish` | add-handover gate — same reason |

### Per CounterCreator objective
| Fact | Source | Notes |
|---|---|---|
| `hasSavageRole` | **[outline]** | `Kills.SavageRoles != null` — captured whenever present, so it round-trips |
| `target` (+ difficulty) | **[outline]** | `Kills.Target`; difficulty is a `ValidTargets` lookup by name |
| `bodyParts` / `daytime` / `distance` | **[outline]** | the decided values, or null = "not decided, treat as vanilla" |
| weapon (`weaponCategories`/`weaponCategory`/`pinnedWeapons`) | **[outline]** | |
| `requiredGear` | **[outline]** | |
| sub-condition *slots* exist (Kills/Daytime/Distance/Weapon) | **[vanilla]** | but schema-guaranteed on any real Kills condition, so effectively constant — rarely a real read |
| `count` | **[outline]**, seeded by extraction | Extraction puts the **vanilla** `task.Value` into `outline.Count` untouched; the roll's count step then scales it (target-swing + ± die) **in place**. So the *rolled* outline holds the scaled value, but rolling reads/writes the outline field — it never reaches into the vanilla quest for the base. A fresh roll always starts from a fresh extraction, so scaling is scale-once, never compounds. (An already-scaled count only exists in the converter, which pins the cached final as-is and does not re-scale.) |
| `existingGear` shape (inclusive vs forbidden-only) | **[vanilla]** | gates the edit-existing-gear branch; only the *decision* (rerolled pieces) is in the outline |

### Per HandoverItem objective
| Fact | Source | Notes |
|---|---|---|
| decided item / category / FIR | **[outline]** | `Handover.*` |
| `originalItem` | **[vanilla]** | needed to pick a tier/price-appropriate replacement, and for the blacklist gate |
| valid / not quest-item / not blacklisted | **[vanilla]** + config | `itemDB[originalItem]` + `HandoverItemBlacklist` (item **and** parent) |
| `count` | **[outline]**, seeded by extraction | vanilla at extraction, scaled by the roll in place — same as kills |

> **Key finding:** the entire per-objective **decision** set is `[outline]` — locale generation already
> proves it round-trips. Counts are `[outline]` too: extraction seeds them from vanilla, and the roll
> scales that seeded field in place, so rolling never reads the vanilla quest for a count. The only
> `[vanilla]` reads left are *shape facts that are not decisions*: the two add-objective gates,
> `hasKillsFailstate`, and existing-gear shape. These are deliberately absent from the sparse outline
> (so it keeps tracking upstream), and vanilla is present at roll time anyway as the input — so they
> stay read-only glances at the input, never schema.

---

## 2. Legend

`[V]` vanilla fact · `[C]` config value · `[R]` RNG draw · `[P]` output of a prior decision this roll
→ **bold** = what it writes into the outline.

---

## 3. The decision tree (in roll order)

```
EditQuest(facts)
│
├─ GATE 0  isBlacklisted[V] && !force              → empty outline, STOP
│
├─ DECISION A · add a kill objective  (quest-level)
│    gate: !hasAnyKills[V] && rand<AddKillObjectiveToQuestChance[C] && !hasKillsFailstate[V]
│    ├─ target  = RollTargetEntry()                 [R] weighted over ValidTargets
│    ├─ count   = AddKillObjectiveKillCount[C]
│    │            + depth[V]*DepthFactor[C] + level[V]*LevelFactor[C]
│    └─ emit **Added Kills objective** (fresh ids) → then flows through the per-objective Kills tree below
│
├─ DECISION B · add a handover objective  (quest-level)
│    gate: !hasAnyHandover[V] && rand<AddHandOverObjectiveToQuestChance[C]
│    ├─ item  = random tier-5 item                  [R]
│    ├─ count = AddHandOverObjectiveBaseCount[C] + depth/level scaling[V][C]
│    └─ emit **Added Handover objective**; forces its per-objective edit to run (override)
│
├─ FOR EACH CounterCreator objective → Kills tree
│    │  scan sub-conditions → {hasKills, hasSavageRole[V], location?, equipment?}   (InZone/HealthEffect are purged)
│    │
│    ├─ GATE C  no Kills sub-cond || hasKillsFailstate[V] || rand>=ChanceToEditKillConditions[C]
│    │            → Added? emit ids+base only. Vanilla? emit nothing (regeneration leaves it alone). STOP objective
│    │
│    ├─ DECISION D · location
│    │    ├─ if none && rand<ChanceToAddLocations[C] && !hasSavageRole[V]:
│    │    │       maps = LocationCount[C] unique from (isEasyQuest? EasyMaps : ValidMaps)   [R]
│    │    ├─ elif present: reroll same way (unless hasSavageRole → skip)
│    │    └─ Sandbox ⇒ +Sandbox_high (derived, NOT stored) → **maps**;  export whatLocations[P]
│    │
│    ├─ DECISION E · gear
│    │    ├─ if none && rand<ChanceToAddGear[C] && !isEasyQuest[V]:
│    │    │       gear = AddGearCount[C] weighted pieces  [R]  → **requiredGear**
│    │    └─ elif existingGear[V] && rand<ChanceToEditGear[C] && !isEasyQuest[V]:
│    │            reroll pieces → **requiredGear**
│    │
│    ├─ EditKillsDetails
│    │    ├─ DECISION F · target   (if current target is a ValidTarget[V])
│    │    │      hasSavageRole[V] → keep, lock.  else roll new[R] → **target**,
│    │    │      export killsEnemyTypeDistance = prevDiff[V] − newDiff  [P]
│    │    ├─ DECISION G · bodypart (if hasBodyPart[V] && !hasSavageRole && !isEasyQuest)
│    │    │      existing → reroll[R]; else rand<ChanceToAddBodypart[C] → add[R]  → **bodyParts**
│    │    ├─ DECISION H · time     (if daytime present[V] && !hasSavageRole)
│    │    │      factory/lab[P] → clear; 0/0 → maybe add(rand<ChanceToAddTimeOfDay[C],!easy); else reroll
│    │    │      window = From[R], To=From+6  → **daytimeFrom/To**
│    │    ├─ DECISION I · distance (if distance present[V])
│    │    │      factory/lab/easy[P][V] → clear(0); existing>0 → reroll; else rand<ChanceToAddDistance[C] → add
│    │    │      compare = ≥|≤ [R], value = (2..5)*10 [R]  → **distanceCompare/Value**
│    │    └─ DECISION J · weapon   (if weapon slot present[V] && (hasWeapon[V] || !isEasyQuest))
│    │           trigger: hasWeapon[V] → always; else rand<ChanceToAddWeapon[C]
│    │           AddRandomWeaponGroup:
│    │           ├─ rand<ChanceForMysteryArsenal[C] && pool>0:
│    │           │     guaranteed pistol + shotgun + MysteryArsenalWeaponCount[C] random [R] → **pinnedWeapons**
│    │           └─ else: weighted category pick [R] → **weaponCategory**
│    │
│    └─ COUNT  (if !hasSavageRole[V])
│           base = outline.count  (seeded from vanilla task.Value by extraction — NOT re-read from vanilla here)
│           × target-swing from killsEnemyTypeDistance[P] (×KillCountWhenTargetTypeChangesFactor[C], floor 5)
│           × generateValueAdjustmentInt(AdjustKillCountFactorsUpDown[C])  [R]   → **count** (scales the seeded field in place)
│
├─ FOR EACH HandoverItem objective
│    ├─ gate: rand>ChanceToEditHandoverCondition[C] && !override → vanilla, STOP
│    ├─ validate originalItem[V] (in db, not quest-item, not blacklisted item/parent)
│    └─ RandomizeHandover:
│          ├─ rand<ChanceToRequireItemCategory[C]: random category → **categoryName**; count ×= ItemCategoryMultiplier[C]
│          └─ else: tier[V] (or from price[V]) → random item → **itemIds**
│          rand<ChanceHandoverNeedsFIR[C] → **foundInRaid**
│          count × generateValueAdjustmentInt(AdjustHandoverCountFactorsUpDown[C]) [R] → **count**
│
└─ QUEST-LEVEL FINALIZE  (regeneration already redoes all of this — NOT decisions)
     purge FindItem (keep quest-items) · purge optional(parentId) · location→quest.Location
```

---

## 4. The helper set (replaces the flags book)

Every fact in §1 is a small pure helper. The ones that *used* to force a mutable object — the
"reads-of-own-writes" — are simply helpers that read the **partial outline**, with a vanilla fallback
so "asked before decided" returns the vanilla value rather than a wrong null:

```csharp
// decisions read straight off the outline being built:
bool    HasSavageRole(ObjectiveOutline o)      => o.Kills?.SavageRoles is { Count: > 0 };
string? DecidedTarget(ObjectiveOutline o)      => o.Kills?.Target;

// the former intra-roll "dependencies" — now just partial-outline reads with a vanilla fallback:
List<string> EffectiveMaps(Quest v, ObjectiveOutline o)
    => o.Kills?.Maps ?? VanillaMaps(v, o.ConditionId);                 // maps decided this roll, else vanilla's
bool IsFactoryOrLab(Quest v, ObjectiveOutline o)                        // used by time + distance
    => EffectiveMaps(v, o).Any(m => m is "factory4_day" or "factory4_night" or "laboratory");
int  TargetDifficultyDelta(Quest v, ObjectiveOutline o)                 // used by count scaling
    => Difficulty(VanillaTarget(v, o.ConditionId)) - Difficulty(o.Kills?.Target ?? VanillaTarget(v, o.ConditionId));

// the handful of pure shape facts — read-only glances at the vanilla input:
bool HasKillsFailstate(Quest v)   => /* Kills in Conditions.Fail */;
bool HasAnyKills(Quest v)         => /* any Kills in AvailableForFinish */;
int  CanonicalCount(Quest v, MongoId condId) => /* task.Value on the vanilla condition */;
```

The rule for every helper: **outline field `?? vanilla value`.** That single pattern is what lets the
roll carry zero state between decisions — the "memory" is just the outline you're already writing, and
the fallback makes ordering forgiving instead of fragile. `whatLocations`, `killsEnemyTypeDistance`,
and the rest of `RandomizeFlags` were caches for exactly these; the helpers make the caches redundant.

---

## 5. Why this is simpler (the reassessment)

What **deletes**:
- `RandomizeFlags` — it exists only to carry decisions from the mutation to the extractor. Its fields
  are (a) list-index bookkeeping into the condition array, (b) decision outputs, (c) the §4 deps.
  Outline-native: (a) is moot (we read facts, not indices), (b) *is* the outline, (c) is function args.
- `QuestOutlineExtractor` — no scratch condition to read decisions back out of.
- The scratch-quest clone + in-place mutation in the roll path (regeneration still clones vanilla, once).
- The dual-write comments/契约 threaded through `QuestEditor` and `QuestTools`.

What **stays**: the gates, the RNG draws, the config, the QuestTools *builders* — but the builders move
to the **regeneration** side (they already live there for Added objectives), so the roller stops
building conditions at all. `QuestTools.Randomize*` collapse into small `Decide*(facts,cfg,rng)->fields`
functions.

Migration path (when usage allows): keep the current roller and the self-test; add the outline-native
roller beside it; run both on the same RNG seed / same quest set and diff their outlines with the
existing harness. When they match, delete the old roller + flags + extractor. Same "prove then delete"
discipline that got us here.

**Net:** three components removed, the roller becomes pure, and "roll on the extracted outline" is
literally the §1 facts → §3 tree → outline. The dual-write was the scaffold; this is the building.
