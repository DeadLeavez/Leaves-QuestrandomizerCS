using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Pipeline;

// THE CONTRACT between the two halves of quest randomization.
//
// The roller runs once (on a fresh roll or a re-roll) and writes one of these per quest. The builder
// runs on EVERY server start and turns { vanilla quest + this record + the CURRENT database } back
// into a finished quest. The two never call each other - this file is the only channel, and the
// builder must be able to work from it with the roller not present at all.
//
// WHAT GOES IN HERE: decisions only - the things a die was rolled for. Anything that can be
// recomputed from the vanilla quest, the config, or the item database is deliberately ABSENT, so it
// re-derives itself every startup and therefore tracks reality. That is the entire point:
//   - a weapon category is stored as its NAME, never as the resolved weapon id list, so a weapon
//     added by a mod update joins every quest that references that category, with no re-roll;
//   - objective text is not stored at all - it is regenerated from these decisions each build, so a
//     wording fix reaches every existing quest;
//   - objectives this mod never touches aren't mentioned here, so they keep tracking vanilla and
//     pick up upstream fixes instead of being frozen at whatever they looked like on roll day.
//
// WHAT MUST NOT GO IN HERE: anything derived. If it can be recomputed, recomputing it is what makes
// the system adapt. (Example: Ground Zero ships as "Sandbox" + "Sandbox_high" but is one map to the
// player. Meta stores "Sandbox"; expanding it back out is a deterministic builder step.)
//
// WHAT MUST BE PINNED: the ids, and anything the player is already relying on. Quest progress in the
// profile is keyed by condition id (PmcData.Quests[].CompletedConditions and TaskConditionCounters),
// and so is objective locale text - so every id this mod MINTS is written down here and reused
// forever. Re-minting them on a rebuild would silently orphan every player's progress. Ids that came
// from vanilla are stable on their own and are only referenced, never stored as "ours".
public sealed record QuestOutline
{
    // ================================ OUTLINE VERSION HISTORY ================================
    // What each version means, and why the bump happened. Bumping CurrentVersion re-rolls every LOCKED
    // (not-yet-accepted) quest so it picks up the change below; started/active quests keep their roll.
    // Add a line here every time you raise CurrentVersion.
    //
    //   1.0  - Initial version. Baseline outline pipeline; no re-roll-on-version behaviour yet.
    //   1.1  - Large expansion of the boss/follower weapon groups: every boss's current weapons + their
    //          frag grenades added to the Arsenal categories, new boss Arsenals (Shadow of Tagilla,
    //          Vengeful Killa, The Wedge, Cultist Priest), and per-boss follower weapon categories.
    //          Bumped so locked quests re-roll and can draw from the new/expanded categories.
    // =========================================================================================
    //
    // THE current outline version, and the ONE public, easy-to-find place to change it. Raise this
    // (e.g. 1.1 -> 1.2, or 1.9 -> 2.0) whenever a mod update should re-roll players' not-yet-accepted
    // quests - e.g. new weapon categories or roll odds a locked quest should pick up. `OutlineVerification`
    // re-rolls any stored outline carrying a LOWER number, but only when the quest is still LOCKED (never
    // started); started or active quests keep their roll so live progress is never disturbed. So a version
    // bump forces a fresh roll for locked quests only - it does NOT force everyone into a re-roll.
    //
    // It is a double so small "just re-roll locked quests" bumps can be a decimal step (1.1, 1.2, ...)
    // without inflating a more meaningful whole-number version. Compared only with < / >= (never ==), so
    // floating-point representation never matters. Additive schema changes that should NOT trigger
    // re-rolls (a new optional decision field, absorbed by the "unknown fields ignored, missing fields
    // default" rules below) do not need a bump - leave this alone for those.
    public const double CurrentVersion = 1.1;

    // The version this outline was rolled under. Stamped automatically from CurrentVersion above: every
    // `new QuestOutline` takes this default, so a fresh roll always records the current version with no
    // extra wiring. A record loaded from disk keeps the number it was saved with (System.Text.Json
    // overwrites the default only when the JSON carries the field; a legacy integer `1` reads back as
    // 1.0), which is how the re-roll above tells a stale roll from a current one.
    [JsonPropertyName("version")]
    public double Version { get; set; } = CurrentVersion;

    [JsonPropertyName("questId")]
    public MongoId QuestId { get; set; }

    // Present ONLY on a mod-invented (procedural) quest - null for every edited-vanilla one. It is the
    // stand-in for the thing a vanilla quest has and a procedural one does not: a base row in the
    // database. A vanilla outline says "find quest X and reshape it"; there is nothing to find for a
    // procedural quest, so this header carries what the builder would otherwise have read off that
    // missing row - who gives it, where it sits in its chain, and the one reward the roll pinned.
    // Everything derivable from those (name, description, XP, the prerequisite wiring) is still
    // recomputed at build time and deliberately absent here, exactly as for a vanilla quest.
    [JsonPropertyName("procedural")]
    public ProceduralHeader? Procedural { get; set; }

    // Sparse and keyed by id - NEVER positional. The roller deletes objectives (FindItem and optional
    // tasks get purged) and appends others, so a final quest's indices do not line up with the
    // vanilla quest's. Keying by id also degrades gracefully across game updates: if an objective we
    // have a record for disappears upstream, the record simply matches nothing and is skipped; if a
    // new one appears, it is built untouched.
    [JsonPropertyName("objectives")]
    public List<ObjectiveOutline> Objectives { get; set; } = new();
}

// The base identity of a procedural quest - the parts a vanilla quest keeps in the database and a
// mod-invented one has nowhere else to live. Kept as small as the rest of the schema: only what the
// builder cannot recompute goes here.
//
//   DERIVED, so NOT here: the quest name/description/messages (built from Slot + Index locale keys),
//   the XP reward (QuestGen_XPPerQuest * Index), the distance/kill-count formulas (they land in the
//   objective's KillsOutline as final values), and the prerequisite Quest CONDITION itself (rebuilt
//   from ChainAfter each boot).
//
//   PINNED, so here: the reward ITEM (a die was thrown over a tier pool - re-rolling it would change
//   the promised reward under a player mid-quest) and every id the player's profile keys progress to.
public sealed record ProceduralHeader
{
    // Which generated chain this quest belongs to ("QG_WeaponMastery", "QG_Sniper", "QG_Fetch", or a
    // future one). Doubles as the locale-key prefix, so the builder re-derives name/description text
    // from it every boot - a wording fix reaches every existing procedural quest with no re-author.
    [JsonPropertyName("slot")]
    public required string Slot { get; set; }

    // Position within that chain (0-based). Drives the derived pieces - reward tier, XP, distance, the
    // "(name) N" suffix - so those stay out of the file and re-derive from Slot + this.
    [JsonPropertyName("index")]
    public required int Index { get; set; }

    // The trader who offers it. Config-driven today (one trader for all generated quests), but stored
    // rather than re-read so a per-chain trader later is just a different value here, no schema change.
    [JsonPropertyName("trader")]
    public required MongoId Trader { get; set; }

    // The quest this one unlocks after - its predecessor in the combined chain. Null for the very
    // first generated quest (no prerequisite). The builder rebuilds the actual "Quest" start-condition
    // from this each boot, so re-threading a changed chain never needs a re-author.
    [JsonPropertyName("chainAfter")]
    public MongoId? ChainAfter { get; set; }

    // The pinned id of that rebuilt prerequisite condition. Minted once at author time and reused, so
    // the start-condition id does not churn every boot. Null when ChainAfter is null (no prerequisite).
    [JsonPropertyName("linkConditionId")]
    public MongoId? LinkConditionId { get; set; }

    // The reward item the roll pinned (one tpl, given found-in-raid, count 1 - matching how generated
    // quests have always rewarded). Pinned because it is a die over a tier pool; re-resolving it each
    // start could swap the reward while the player is working toward it.
    [JsonPropertyName("rewardItem")]
    public MongoId? RewardItem { get; set; }

    // The ids the reward builders MINT - the XP reward's id, and the item reward's id plus the id
    // shared by its wrapper item and the reward's Target. A procedural quest rebuilds its rewards from
    // scratch every boot (unlike a vanilla quest, which keeps its own), so these are pinned here for the
    // same reason every other minted id is: without them the rebuild is not byte-identical and the
    // reward ids churn on every restart. Null on a legacy header (pre-dating this field) - the builder
    // falls back to minting fresh, exactly as before.
    [JsonPropertyName("rewardXpId")]
    public MongoId? RewardXpId { get; set; }

    [JsonPropertyName("rewardItemId")]
    public MongoId? RewardItemId { get; set; }

    [JsonPropertyName("rewardItemGroupId")]
    public MongoId? RewardItemGroupId { get; set; }
}

// One decision record per objective THIS MOD had a hand in. Objectives it leaves alone never appear.
public sealed record ObjectiveOutline
{
    // Where this objective CAME FROM. "Vanilla" = the base quest already has it and the builder
    // reshapes it in place; "Added" = the randomizer invented it, so the builder must create it.
    //
    // The distinction is load-bearing for the "add" case: the roller only adds a kill/handover
    // objective if the quest lacks one AND a chance roll passes. On a rebuild the vanilla quest still
    // lacks it, so without a record saying "we added one, here is its id", the builder would either
    // skip it or re-roll the chance - and quests would gain and lose objectives on every restart.
    // The converter sits on the PROPERTY, not on the enum type. System.Text.Json ranks a
    // property-level converter above options.Converters, which in turn outranks a type-level
    // attribute - and SPT's JsonUtil registers its own converters, so a type-level one loses.
    [JsonPropertyName("origin")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ObjectiveOrigin Origin { get; set; }

    [JsonPropertyName("objectiveType")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ObjectiveType Type { get; set; }

    // For "edit": the vanilla condition id to find. For "add": the id the builder must give the
    // objective it creates. Either way this is the id the player's progress and the objective's
    // locale text hang off, so it is fixed at roll time and never regenerated.
    [JsonPropertyName("conditionId")]
    public required MongoId ConditionId { get; set; }

    // Ids for the sub-parts of a CounterCreator objective, recorded whatever their origin. The
    // builder treats each as FIND-OR-CREATE: a vanilla sub-condition is found by this id, one this
    // mod minted isn't present in the vanilla quest so gets created with it. That single rule covers
    // both cases without the record having to remember which of the two it was, and either way the
    // id - which the player's progress and the objective's text hang off - stays fixed forever.
    // Null means this objective simply doesn't have that sub-condition.
    [JsonPropertyName("counterId")]
    public MongoId? CounterId { get; set; }

    [JsonPropertyName("killsConditionId")]
    public MongoId? KillsConditionId { get; set; }

    [JsonPropertyName("locationConditionId")]
    public MongoId? LocationConditionId { get; set; }

    [JsonPropertyName("gearConditionId")]
    public MongoId? GearConditionId { get; set; }

    // Exactly one of these is set, matching Kind. Kept as separate nullable payloads rather than one
    // polymorphic blob so adding a THIRD objective kind later is a new property plus a new builder
    // case - old records keep deserializing untouched.
    [JsonPropertyName("kills")]
    public KillsOutline? Kills { get; set; }

    [JsonPropertyName("handover")]
    public HandoverOutline? Handover { get; set; }
}

// Serialized BY NAME, not by ordinal. Two reasons: the outline is meant to be readable and
// hand-editable, and an ordinal would silently change meaning for every already-stored record the
// moment anyone reordered or inserted a member here.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectiveOrigin
{
    Vanilla,
    Added
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObjectiveType
{
    Kills,
    Handover
}

// Every die the roller casts for a kill objective. Absent (null/empty) consistently means "this was
// not rolled, leave whatever vanilla had" - which is what makes new optional decisions safe to add.
public sealed record KillsOutline
{
    // Final required kill count, AFTER the target-swap and up/down adjustment maths. Stored as the
    // OUTCOME, not as the inputs, because re-deriving it would need the same RNG the roll used.
    [JsonPropertyName("count")]
    public int? Count { get; set; }

    // Who to kill: a plain target ("AnyPmc", "Savage") and/or specific bot roles for boss hunts.
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("savageRoles")]
    public List<string>? SavageRoles { get; set; }

    [JsonPropertyName("bodyParts")]
    public List<string>? BodyParts { get; set; }

    // Pinned: it is the roll, and it is what the player reads in the objective.
    [JsonPropertyName("maps")]
    public List<string>? Maps { get; set; }

    [JsonPropertyName("distanceCompare")]
    public string? DistanceCompare { get; set; }

    [JsonPropertyName("distanceValue")]
    public double? DistanceValue { get; set; }

    [JsonPropertyName("daytimeFrom")]
    public int? DaytimeFrom { get; set; }

    [JsonPropertyName("daytimeTo")]
    public int? DaytimeTo { get; set; }

    // THE late-bound field, and a big reason this whole design exists: one or more weapon category
    // NAMES. The builder resolves them against the live category membership every start, so a weapon
    // added by a content mod - or one that joins because a caliber selector now matches it - is usable
    // for this quest immediately, with no re-roll and no cache invalidation. A single entry means that
    // category with its full mod requirements; two or more mean "a weapon from ANY of these" - the
    // union of their weapon lists with NO mod requirements (a multi-roll only ever picks categories
    // that HAVE no mods, so nothing is dropped).
    [JsonPropertyName("weaponCategories")]
    public List<string>? WeaponCategories { get; set; }

    // An explicit SET of specific weapon ids rather than a named category - nothing to late-bind to.
    // The mystery-arsenal roll produces it (guaranteed pistol + shotgun + a few random weapons), and it
    // is equally usable for a hand-authored fixed weapon set. When set, it is the requirement verbatim.
    [JsonPropertyName("pinnedWeapons")]
    public List<MongoId>? PinnedWeapons { get; set; }

    // Marks the objective's PinnedWeapons as a rolled mystery arsenal (as opposed to a hand-authored
    // pinned set). Purely a flag for downstream code to recognise the case later - regeneration builds
    // the requirement from PinnedWeapons the same way either way.
    [JsonPropertyName("mysteryArsenal")]
    public bool MysteryArsenal { get; set; }

    // Gear the player must be wearing, expressed as a NAMED gear group (from gearcategories.jsonc).
    // Late-bound exactly like a weapon category: regeneration expands it against the group's live item
    // list, so an item added to the group becomes acceptable for every quest that rolled it with no
    // re-roll, and the locale renders the group's translated label instead of a wall of item names.
    // This is what the roller writes now.
    [JsonPropertyName("requiredGearCategory")]
    public string? RequiredGearCategory { get; set; }

    // LEGACY: a set of specific gear-piece ids (each an alternative - "wear any one of these"), the
    // shape the pre-categories roller produced. No longer written by a fresh roll; kept so cached
    // outlines still regenerate until OutlineVerification.FixSpecificGear re-rolls them into a
    // RequiredGearCategory. When RequiredGearCategory is set, this is ignored.
    [JsonPropertyName("requiredGear")]
    public List<MongoId>? RequiredGear { get; set; }
}

// Hand-over objectives. The item is PINNED on purpose: re-resolving it from a tier each start could
// change what the player has to bring while they are half-way through collecting it.
public sealed record HandoverOutline
{
    // Set when the roll chose a CATEGORY rather than one specific item. Late-bound exactly like a
    // weapon category: the builder expands it against live membership, so an item added to a handover
    // category becomes acceptable for every existing quest that rolled it, with no re-roll.
    // When this is set, ItemIds is ignored.
    [JsonPropertyName("categoryName")]
    public string? CategoryName { get; set; }

    // The concrete item(s) rolled when it was NOT a category. Pinned - re-resolving from a tier each
    // start could change what the player must bring while they are half-way through collecting it.
    [JsonPropertyName("itemIds")]
    public List<MongoId>? ItemIds { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    [JsonPropertyName("foundInRaid")]
    public bool? FoundInRaid { get; set; }
}
