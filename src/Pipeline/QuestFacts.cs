using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using Leaves_Questrandomizer.Data;

namespace Leaves_Questrandomizer.Pipeline;

// The fact layer for the outline-native roller (see docs/rolling-decision-tree.md).
//
// Every fact a roll needs is DERIVED ON DEMAND here, from one of exactly two read-only sources - never
// from a persisted accumulator. This is what lets the roll carry no state between decisions (no
// RandomizeFlags, no facts book): the "memory" is just the outline being built, read back through these
// helpers.
//
//   [outline] the rolled outline SO FAR - the single source of truth for anything the roll decided.
//   [vanilla] the vanilla quest - read-only input, consulted only for shape facts that are NOT
//             decisions (the two add gates, kill-failstate, canonical values a decision then mutates,
//             existing-gear shape).
//
// THE INVARIANT: these are pure functions of their arguments. They hold no per-roll mutable state and
// must never be given any. If a value has to survive between decisions, that makes it a decision, and
// it belongs in the outline (which these then read back), not a field here.
//
// A few facts need config or the precomputed quest graph (target difficulty, easy/blacklist, depth),
// which now come from the static Settings / static QuestTools - so this whole class is static too, with
// only the item database handed in once via Init. The methods stay pure over (vanillaQuest,
// rolledOutline) + that immutable global state.
//
// THE PER-OBJECTIVE READS LIVE IN ObjectiveFacts, not here. Everything of the form "what does this
// objective require - decision first, quest second" is a pure static over (quest, outline) with no
// config in it at all, and QuestLocaleGeneration needs exactly those same reads to describe a quest
// (it was rendering raw outline fields, so every requirement the roll never touched went unsaid). Kept
// as its own class on purpose (a clean split), NOT re-merged here. This class forwards the roller-facing
// ones to it, so roller call sites read exactly as they did before.
public static class QuestFacts
{
    private static Dictionary<MongoId, TemplateItem> itemDB = null!;
    private static string modPath = null!;
    private static Leaves.JsonIdAnnotator annotator = null!;

    // Handed the item database once, from Questrandomizer.OnLoad (after Settings.Init / QuestTools.Init,
    // which its methods read). Read-only afterwards. modPath is where the forced-item-maps disk cache
    // lives, and the annotator turns that cache into readable JSONC when it is (re)built (see
    // ForcedItemMaps).
    public static void Init(Dictionary<MongoId, TemplateItem> items, string modFolderPath, Leaves.JsonIdAnnotator idAnnotator)
    {
        itemDB = items;
        modPath = modFolderPath;
        annotator = idAnnotator;
    }

    // ---- Quest-level shape [vanilla] / config / graph -------------------------------------------

    // Config gates, keyed purely by id - no vanilla quest object needed, so they take just the id.
    public static bool IsBlacklisted(MongoId questId)
    {
        return Settings.Config.QuestBlacklist.Contains(questId);
    }

    public static bool IsEasyQuest(MongoId questId)
    {
        return Settings.Config.EasierQuestList.Contains(questId);
    }

    // Position in the vanilla quest graph (precomputed once, structural, not per-profile). Drives the
    // deterministic count of an ADDED objective. Absent = treated as 0, matching the roller's own guard.
    public static int Depth(MongoId questId)
    {
        return QuestTools.GetQuestDepth(questId)?.Depth ?? 0;
    }

    public static int Level(MongoId questId)
    {
        return QuestTools.GetQuestDepth(questId)?.Level ?? 0;
    }

    // A Kills in the FAIL bucket ("fail if you kill X") locks out kill edits/adds - killing is the fail
    // condition, so randomizing it would invert the quest. Uses the same deep search the roller does.
    public static bool HasKillsFailstate(Quest vanilla)
    {
        List<QuestCondition>? fail = vanilla.Conditions?.Fail;
        if (fail == null)
        {
            return false;
        }
        return Leaves.Utils.Instance.searchObject("Kills", fail);
    }

    // The two ADD-objective gates. The outline is sparse - an untouched vanilla objective is absent from
    // it - so "does this quest already have a kill/handover objective" can only be answered from the
    // vanilla quest. Same deep-search predicate the roller gates on today.
    public static bool HasAnyKills(Quest vanilla)
    {
        List<QuestCondition>? tasks = vanilla.Conditions?.AvailableForFinish;
        if (tasks == null)
        {
            return false;
        }
        return Leaves.Utils.Instance.searchObject("Kills", tasks);
    }

    public static bool HasAnyHandover(Quest vanilla)
    {
        List<QuestCondition>? tasks = vanilla.Conditions?.AvailableForFinish;
        if (tasks == null)
        {
            return false;
        }
        return Leaves.Utils.Instance.searchObject("HandoverItem", tasks);
    }

    // ---- Target difficulty [config] -------------------------------------------------------------

    public static bool IsValidTarget(string? target)
    {
        if (target == null)
        {
            return false;
        }
        foreach (ValidTargetEntry entry in Settings.Misc.ValidTargets)
        {
            if (entry.Target == target)
            {
                return true;
            }
        }
        return false;
    }

    // Null when the target isn't one we recognise - the caller treats that as "no difficulty swing",
    // exactly as the roller does when a quest's original target isn't in the ValidTargets list.
    public static int? Difficulty(string? target)
    {
        foreach (ValidTargetEntry entry in Settings.Misc.ValidTargets)
        {
            if (entry.Target == target)
            {
                return entry.Difficulty;
            }
        }
        return null;
    }

    // ---- Reads with a vanilla fallback (outline decision ?? vanilla) ----------------------------
    // The core pattern, and the reason NOTHING needs "seeding": read the decision made earlier this
    // roll, falling back to the vanilla value when it hasn't been decided yet. The `?? vanilla` makes
    // "asked before decided" return the vanilla answer instead of a wrong null, so ordering is forgiving
    // rather than fragile - and an EMPTY outline still answers every one of these correctly by falling
    // through to the vanilla quest. Every fact a decision can set lives in ObjectiveFacts; these forward
    // to it with the VANILLA quest as the source, which is the right source during a roll.

    // Boss/role hunt? Set by the roll, but inherently a vanilla shape fact too - so outline-then-vanilla.
    // When true it locks out location, target-swap, bodypart and time.
    public static bool HasSpecialTargetType(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.HasSpecialTargetType(vanilla, rolled);
    }

    // The target in effect: the one the roll decided, else vanilla's.
    public static string? GetTarget(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetTarget(vanilla, rolled);
    }

    // The maps in effect for this objective: the ones the roll decided, else whatever vanilla listed.
    public static List<string> GetMaps(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetMaps(vanilla, rolled);
    }

    // Factory/Labs (and easy quests, handled by the caller) suppress the time and distance windows -
    // there is no long sightline or day/night cycle to gate on. Reads the maps decided so far.
    public static bool IsFactoryOrLab(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.IsFactoryOrLab(vanilla, rolled);
    }

    // The kill count in play for scaling: the roll's decided count, else the raw vanilla count. (There
    // is no "seeding" - the `?? vanilla` IS how the vanilla count enters; nothing is pre-copied.)
    public static int? GetKillCount(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetKillCount(vanilla, rolled);
    }

    // Current kill DISTANCE value - the clear/reroll/add branch keys on whether it's > 0. Null = the
    // vanilla condition carries no distance at all (the whole distance step is then skipped).
    public static double? GetDistanceValue(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetDistanceValue(vanilla, rolled);
    }

    // Current kill TIME-OF-DAY window (From, To). (0, 0) means "no window" - the roll treats a genuinely
    // absent daytime and a present-but-unset one identically, so there is nothing to gain from telling
    // them apart.
    public static (int From, int To) GetDaytime(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetDaytime(vanilla, rolled);
    }

    // Current kill BODY-PART requirement - the reroll-vs-add branch keys on whether it's non-empty.
    public static List<string> GetBodyParts(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetBodyParts(vanilla, rolled);
    }

    // The handover twin of GetKillCount - reads the Handover payload, and the count sits on the
    // task itself (not a sub-condition), so the vanilla fallback is the task's own Value.
    public static int? GetHandoverCount(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetHandoverCount(vanilla, rolled);
    }

    // Handover found-in-raid in effect. The roll only ever FORCES it true, so the fact is simply the
    // current value: the roll's if decided, else vanilla's.
    public static bool GetFoundInRaid(Quest vanilla, ObjectiveOutline rolled)
    {
        return ObjectiveFacts.GetFoundInRaid(vanilla, rolled);
    }

    // The difficulty swing a target swap produces, used to scale the kill count. Matches the roller:
    // (previous target's difficulty) - (new target's difficulty), and 0 whenever the previous target
    // isn't a recognised one. "Previous" is the vanilla target; "new" is the decided one (or vanilla,
    // if not yet swapped, giving a swing of 0).
    public static int TargetDifficultyDelta(Quest vanilla, ObjectiveOutline rolled)
    {
        string? previous = GetVanillaTarget(vanilla, rolled.ConditionId);
        int? previousDifficulty = Difficulty(previous);
        if (previousDifficulty == null)
        {
            return 0;
        }
        string? current = GetTarget(vanilla, rolled);
        int? currentDifficulty = Difficulty(current);
        if (currentDifficulty == null)
        {
            return 0;
        }
        return previousDifficulty.Value - currentDifficulty.Value;
    }

    // ---- Vanilla per-objective shape [vanilla] --------------------------------------------------
    // Read-only glances at the roll's input, located by the outline's pinned condition id.

    // The raw vanilla kill count for this objective - the fallback source for GetKillCount.
    public static int? GetVanillaKillCount(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.GetQuestKillCount(vanilla, conditionId);
    }

    // Does vanilla have a Location sub-condition here at all? Distinguishes "add a new location" from
    // "reroll the existing one". GetVanillaMaps returns an empty list for BOTH "no condition" and "present
    // but empty", so it can't answer this on its own.
    public static bool HasVanillaLocation(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.HasQuestLocation(vanilla, conditionId);
    }

    // Does vanilla have an Equipment sub-condition here at all (inclusive OR forbidden-only)? Gear-adding
    // needs to tell "no condition" (safe to add) from "a forbidden-gear-only condition" (leave alone) -
    // HasVanillaIncludeGear can't, since a forbidden-only condition has no inclusive group either.
    public static bool HasVanillaEquipment(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.HasQuestEquipment(vanilla, conditionId);
    }

    public static string? GetVanillaTarget(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.GetQuestTarget(vanilla, conditionId);
    }

    // Vanilla already pins a specific weapon set? Then the roll always re-rolls it (vs. a chance to ADD
    // one where there was none). A vanilla fact - the weapon slot before any edit.
    public static bool HasVanillaWeapons(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.HasQuestWeapons(vanilla, conditionId);
    }

    // Vanilla came with its own REQUIRED-gear list (a non-empty inclusive group)? That gates the
    // edit-existing-gear branch. Forbidden-gear-ONLY conditions have no inclusive group and are left
    // alone, so this must check the inclusive side specifically, not just "has an Equipment condition".
    public static bool HasVanillaIncludeGear(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.HasQuestIncludeGear(vanilla, conditionId);
    }

    // Vanilla map list for this objective (the Location sub-condition's targets), else empty. The
    // fallback source for GetMaps.
    public static List<string> GetVanillaMaps(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.GetQuestMaps(vanilla, conditionId);
    }

    // ---- Handover shape [vanilla] ---------------------------------------------------------------

    // The item the vanilla handover asks for (its first target) - what the roll picks a replacement for.
    public static string? GetVanillaHandoverItem(Quest vanilla, MongoId conditionId)
    {
        return ObjectiveFacts.GetQuestHandoverItem(vanilla, conditionId);
    }

    // Can this handover be edited at all? Mirrors the roller's guards: the item must exist in the DB,
    // not be a quest item, and be blacklisted by neither its own id nor its parent's.
    public static bool IsHandoverEditable(Quest vanilla, MongoId conditionId)
    {
        string? itemStr = GetVanillaHandoverItem(vanilla, conditionId);
        if (itemStr == null)
        {
            return false;
        }
        MongoId item = itemStr;
        if (!itemDB.TryGetValue(item, out TemplateItem? template))
        {
            return false;
        }
        if (template.Properties?.QuestItem == true)
        {
            return false;
        }
        if (Settings.Config.HandoverItemBlacklist.Contains(item))
        {
            return false;
        }
        MongoId parent = template.Parent;
        if (Settings.Config.HandoverItemBlacklist.Contains(parent))
        {
            return false;
        }
        return true;
    }

    // ---- Per-objective map, for the "[Map]" suffix on objective text ----------------------------
    // The maps ONE objective is bound to, as display names, for QuestLocaleGeneration.AppendMapSuffixes
    // to bracket onto the objective's text (so vanilla/unrolled kills and find objectives become
    // searchable + filterable by map, in-game and in the web UI). Three-way return:
    //   - a NON-EMPTY list -> the specific map(s) this objective is fixed to
    //   - an EMPTY list     -> mappable but UNBOUND (a location-less kill, or a find whose item has no
    //                          forced spawn): the caller renders "[Any map]"
    //   - null              -> not a map-annotated objective type (handover, plant/zone-only, ...): the
    //                          caller leaves the text alone
    //
    // Sources mirror the quest conditions: a Location sub-condition (kill/survive/exit on a map) is the
    // authority when present; a FindItem resolves through the forced-spawn index. LeaveItemAtLocation is
    // DELIBERATELY not handled - its item resolves to the SOURCE map, not the plant zone, so annotating
    // it would be wrong; the plant map is zone-only and waits on the Tier 3 (client-bundle) work.
    public static List<string>? GetObjectiveMapDisplays(QuestCondition task)
    {
        List<QuestConditionCounterCondition>? subs = task.Counter?.Conditions;
        if (subs != null)
        {
            bool hasKills = false;
            List<string>? locationMaps = null;
            foreach (QuestConditionCounterCondition sub in subs)
            {
                if (sub.ConditionType == "Kills")
                {
                    hasKills = true;
                }
                if (sub.ConditionType == "Location")
                {
                    locationMaps = sub.Target?.List;
                }
            }
            if (locationMaps != null)
            {
                return MapDisplays(locationMaps);
            }
            if (hasKills)
            {
                // A kill with no Location genuinely counts everywhere -> "[Any map]", so the web UI's
                // any-map filter can pick it up (the whole point of annotating unrolled kills).
                return new List<string>();
            }
            // A counter that is neither a kill nor map-fixed (pure exit-status, zone visit) -> leave alone.
            return null;
        }

        if (task.ConditionType == "FindItem")
        {
            var codes = new HashSet<string>();
            Dictionary<string, HashSet<string>> index = ForcedItemMaps();
            foreach (string itemId in TargetItemIds(task))
            {
                // Only QUEST items are the 100%-spawn, map-specific pickups the forced-spawn premise
                // rests on. A COMMON item (a procedural "obtain X" target) can be a forced spawn on some
                // modded map - Icebreaker force-spawns vanilla moonshine on its suburbs, say - which would
                // resolve to a single misleading map; and it's findable anywhere anyway. So skip it, and
                // if nothing quest-item resolves, return null: no bracket at all (not "[Any map]").
                if (!IsQuestItem(itemId))
                {
                    continue;
                }
                if (index.TryGetValue(itemId, out HashSet<string>? itemMaps))
                {
                    foreach (string mapCode in itemMaps)
                    {
                        codes.Add(mapCode);
                    }
                }
            }
            if (codes.Count == 0)
            {
                return null;
            }
            return MapDisplaysFromCodes(codes);
        }

        return null;
    }

    // Is this item id a mission (quest) item - the QuestItem-flagged pickup that force-spawns on exactly
    // its quest's map? Unknown/invalid ids and ordinary items are false, so only true quest pickups get a
    // find-objective map bracket.
    private static bool IsQuestItem(string itemId)
    {
        if (!MongoId.IsValidMongoId(itemId))
        {
            return false;
        }
        MongoId id = itemId;
        if (!itemDB.TryGetValue(id, out TemplateItem? template))
        {
            return false;
        }
        return template.Properties?.QuestItem == true;
    }

    // Engine map ids -> display names (base.json), collapsing sandbox_high into sandbox first so Ground
    // Zero reads once, and de-duping by display while keeping first-seen order (matches the kill sentence).
    private static List<string> MapDisplays(IEnumerable<string> mapIds)
    {
        var codes = new List<string>();
        foreach (string mapId in mapIds)
        {
            codes.Add(mapId);
        }
        return MapDisplaysFromCodes(codes);
    }

    public static List<string> MapDisplaysFromCodes(IEnumerable<string> mapCodes)
    {
        // Lowercase once up front - the day+night collapse below has to look across all the codes, not
        // just normalize each one in isolation the way sandbox_high does.
        var codes = new List<string>();
        foreach (string rawCode in mapCodes)
        {
            codes.Add(rawCode.ToLowerInvariant());
        }

        // Re-derive the factory NIGHT code the exact way QuestRegeneration.ApplyMaps does, for the same
        // reason: the OUTLINE stores only the chosen day code, so a caller reading the outline (the rolled
        // kill sentence) never sees the night widening the built quest actually got. The quest-condition
        // caller already has both codes, so this is a no-op there. Config-gated and deterministic - never
        // the reverse (a night-only objective has no day code, so it stays "[Factory - Night]").
        if (Settings.Config.FactoryDayQuestsAlsoCountAtNight
            && codes.Contains("factory4_day")
            && !codes.Contains("factory4_night"))
        {
            codes.Add("factory4_night");
        }

        // Factory day + night are one map to the player. When an objective counts on BOTH (a day
        // objective widened by factoryDayQuestsAlsoCountAtNight), collapse the pair to a single plain
        // "factory4" -> "[Factory]". A night-only objective keeps "factory4_night" -> "[Factory - Night]",
        // which is what makes the widening visibly one-directional.
        bool hasFactoryDay = codes.Contains("factory4_day");
        bool hasFactoryNight = codes.Contains("factory4_night");
        bool collapseFactory = hasFactoryDay && hasFactoryNight;

        var result = new List<string>();
        var seen = new HashSet<string>();
        foreach (string rawCode in codes)
        {
            string code = rawCode;
            if (code == "sandbox_high")
            {
                code = "sandbox";
            }
            if (collapseFactory && (code == "factory4_day" || code == "factory4_night"))
            {
                code = "factory4";
            }
            string display = QuestLocaleGeneration.GetLoc(code);
            if (seen.Add(display))
            {
                result.Add(display);
            }
        }
        return result;
    }

    // The DISPLAY names of the maps the given bot ROLES are known to appear on, from enemymaps.jsonc
    // (union across every role, run through MapDisplaysFromCodes so it de-dupes by display name and
    // gets the same factory/Ground-Zero collapse as every other map render). Empty when none of the
    // roles is listed - the caller then keeps its "[Any map]" fallback. This is what turns a boss/Goon/
    // cultist hunt with no Location condition from a misleading "[Any map]" into the handful of maps the
    // target actually spawns on. Lookup is case-insensitive (Settings.EnemyMaps' comparer).
    public static List<string> MapDisplaysForRoles(IEnumerable<string> roles)
    {
        var codes = new List<string>();
        foreach (string role in roles)
        {
            if (Settings.EnemyMaps.TryGetValue(role, out List<string>? roleMaps) && roleMaps != null)
            {
                foreach (string code in roleMaps)
                {
                    codes.Add(code);
                }
            }
        }
        return MapDisplaysFromCodes(codes);
    }

    // The bot ROLES a kill objective hunts, read straight off the served task's Kills sub-condition
    // (no outline needed - used by the vanilla/unrolled map-suffix pass). Empty when the task has no
    // Kills condition or names no role.
    public static List<string> SavageRolesOfTask(QuestCondition task)
    {
        foreach (QuestConditionCounterCondition sub in task.Counter?.Conditions ?? new List<QuestConditionCounterCondition>())
        {
            if (sub.ConditionType == "Kills")
            {
                return sub.SavageRole ?? new List<string>();
            }
        }
        return new List<string>();
    }

    // The concrete item ids a find / leave-at-location objective targets (usually one; a list means
    // "any of these"). Mirrors how ObjectiveFacts reads handover targets: the List first, else the
    // single Item.
    private static IEnumerable<string> TargetItemIds(QuestCondition task)
    {
        List<string>? list = task.Target?.List;
        if (list is { Count: > 0 })
        {
            return list;
        }
        string? single = task.Target?.Item;
        if (single != null)
        {
            return new List<string> { single };
        }
        return new List<string>();
    }

    // questItemId -> the (lowercased) map code names whose FORCED loose-loot spawns include that item.
    // Global world data, identical for every profile, so one shared immutable index is correct - like
    // itemDB / Settings / QuestTools.
    //
    // Building it from the live loot tables is EXPENSIVE: LazyLoad<LooseLoot>.Value re-deserializes a
    // map's entire loose-loot JSON on every access (no built-in caching) AND runs every registered
    // transformer (LootFuckery's included, which then logs its point-item warnings). Walking every map
    // that way stalled first-connect for 10-15s. So we persist the finished index to disk and only pay
    // that cost when the cache is genuinely stale.
    //
    // The index IS its own fingerprint: it holds an entry for EVERY QuestItem-flagged id in itemDB (an
    // empty list when the item has no forced spawn anywhere - see BuildForcedItemMapsFromLoot). So the
    // cache is stale exactly when its key set no longer matches the current quest-item roster, which a
    // cheap itemDB pass decides without touching loot. A quest-item mod -> one rebuild; every other start
    // reads the file and skips loot entirely. NOT sensitive to a new MAP re-spawning an existing quest
    // item (rare, and only costs a missing map suffix) - delete the file to force a rebuild in that case.
    //
    // Written as indented JSONC and run through the JsonIdAnnotator so every id/map line carries a
    // "// what this is" comment - the raw MongoId keys are otherwise write-only. Safe to read back:
    // deserialization below skips comments, the same way SPT's own .jsonc configs load.
    private const string ForcedItemMapsCacheFile = "assets/generated/forcedItemMaps.jsonc";

    private static Dictionary<string, HashSet<string>>? forcedItemMaps;
    private static readonly object forcedItemMapsGate = new();

    private static Dictionary<string, HashSet<string>> ForcedItemMaps()
    {
        if (forcedItemMaps != null)
        {
            return forcedItemMaps;
        }
        lock (forcedItemMapsGate)
        {
            if (forcedItemMaps != null)
            {
                return forcedItemMaps;
            }

            List<string> questItemIds = CurrentQuestItemIds();

            Dictionary<string, HashSet<string>>? cached = LoadForcedItemMapsCache(questItemIds);
            if (cached != null)
            {
                forcedItemMaps = cached;
                return forcedItemMaps;
            }

            // Only reached when the cache is missing/stale, so this is the one path that actually reads
            // every map's loose loot - the slow bit, and the reason LootFuckery's transformer runs and
            // may log its harmless point-item warnings. Say so, so a 10-15s pause here isn't a mystery.
            Leaves.Utils.Instance.printColor(
                "[Questrandomizer] Building the forced-spawn quest-item map lookup from loot tables - this takes a moment, and if LootFuckery is installed it may print some harmless 'point item that doesn't exist' warnings while this runs.",
                LogTextColor.Yellow);

            Dictionary<string, HashSet<string>> index = BuildForcedItemMapsFromLoot(questItemIds);
            SaveForcedItemMapsCache(index);

            Leaves.Utils.Instance.printColor(
                "[Questrandomizer] Forced-spawn quest-item map lookup done (cached to disk; future starts skip this).",
                LogTextColor.Yellow);

            forcedItemMaps = index;
            return forcedItemMaps;
        }
    }

    // The QuestItem-flagged ids currently in the DB - the roster the cache's key set must match. Cheap: a
    // pass over itemDB, no loot.
    private static List<string> CurrentQuestItemIds()
    {
        var ids = new List<string>();
        foreach (KeyValuePair<MongoId, TemplateItem> entry in itemDB)
        {
            if (entry.Value.Properties?.QuestItem == true)
            {
                ids.Add(entry.Key.ToString());
            }
        }
        return ids;
    }

    // Read the disk cache and return its index IFF the file loads and its keys still match the current
    // quest-item roster. Any mismatch, missing file, or parse failure returns null -> caller rebuilds.
    private static Dictionary<string, HashSet<string>>? LoadForcedItemMapsCache(List<string> questItemIds)
    {
        string path = System.IO.Path.Combine(modPath, ForcedItemMapsCacheFile);
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        Dictionary<string, List<string>>? itemMaps;
        try
        {
            // Skip comments / trailing commas: the file is annotated JSONC, so the default (strict)
            // reader would throw on the very "// ..." comments we wrote.
            var readOptions = new System.Text.Json.JsonSerializerOptions
            {
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            itemMaps = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(System.IO.File.ReadAllText(path), readOptions);
        }
        catch (System.Exception)
        {
            // Corrupt/half-written file - treat as absent and rebuild.
            return null;
        }
        if (itemMaps == null)
        {
            return null;
        }

        // The keys ARE the roster fingerprint: every quest item has an entry (empty list if it force-
        // spawns nowhere). Same count + every current quest item present == identical sets, since keys
        // are unique. Any difference means a quest-item mod came or went -> stale.
        if (itemMaps.Count != questItemIds.Count)
        {
            return null;
        }
        foreach (string questItemId in questItemIds)
        {
            if (!itemMaps.ContainsKey(questItemId))
            {
                return null;
            }
        }

        var index = new Dictionary<string, HashSet<string>>();
        foreach (KeyValuePair<string, List<string>> entry in itemMaps)
        {
            index[entry.Key] = new HashSet<string>(entry.Value);
        }
        return index;
    }

    // Write the index as a bare id -> maps map. Best-effort: a write failure just means the next start
    // rebuilds again, so it must never take down profile generation. Keys and map lists are sorted so the
    // file is diff-stable across rebuilds.
    private static void SaveForcedItemMapsCache(Dictionary<string, HashSet<string>> index)
    {
        var serializable = new SortedDictionary<string, List<string>>(System.StringComparer.Ordinal);
        foreach (KeyValuePair<string, HashSet<string>> entry in index)
        {
            var maps = new List<string>(entry.Value);
            maps.Sort(System.StringComparer.Ordinal);
            serializable[entry.Key] = maps;
        }

        try
        {
            string path = System.IO.Path.Combine(modPath, ForcedItemMapsCacheFile);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Indented so the annotator's line-oriented pass can append a comment to each id/map line.
            var writeOptions = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(serializable, writeOptions);
            System.IO.File.WriteAllText(path, annotator.Annotate(json));
        }
        catch (System.Exception)
        {
            // ignore - the cache is an optimization, not a correctness requirement.
        }
    }

    // The expensive path: walk every map's FORCED loose-loot spawns, then project down to the quest-item
    // roster - one entry per quest item, empty when it force-spawns nowhere. The empty entries are what
    // let the key set double as the staleness fingerprint (a quest item that resolves to no map is still
    // "known", so it doesn't look missing and rebuild every start). Doing this lazily (rather than at
    // Init) guarantees every other mod (WTT quest items and the like) has already injected before the
    // LazyLoad<LooseLoot>.Value we read applies their transformers. Forced spawns are the always-spawn
    // (quest/scripted) items, so this carries no ordinary-loot noise.
    private static Dictionary<string, HashSet<string>> BuildForcedItemMapsFromLoot(List<string> questItemIds)
    {
        var raw = new Dictionary<string, HashSet<string>>();
        foreach (string mapCode in Leaves.Utils.Instance.getLocationNames())
        {
            Location? location = Leaves.Utils.Instance.findLocationByCodeName(mapCode);
            LooseLoot? loot = location?.LooseLoot?.Value;
            IEnumerable<Spawnpoint>? forced = loot?.SpawnpointsForced;
            if (forced == null)
            {
                continue;
            }
            foreach (Spawnpoint point in forced)
            {
                IEnumerable<SptLootItem>? items = point.Template?.Items;
                if (items == null)
                {
                    continue;
                }
                foreach (SptLootItem item in items)
                {
                    string itemId = item.Template.ToString();
                    if (!raw.TryGetValue(itemId, out HashSet<string>? maps))
                    {
                        maps = new HashSet<string>();
                        raw[itemId] = maps;
                    }
                    maps.Add(mapCode);
                }
            }
        }

        // Project onto the roster: exactly the quest items, each with its maps or an empty set. Non-quest
        // forced-spawn items (e.g. scripted scenery) are dropped - GetObjectiveMapDisplays never looks
        // them up, and keeping the key set == roster is what makes the fingerprint check exact.
        var index = new Dictionary<string, HashSet<string>>();
        foreach (string questItemId in questItemIds)
        {
            if (raw.TryGetValue(questItemId, out HashSet<string>? maps))
            {
                index[questItemId] = maps;
            }
            else
            {
                index[questItemId] = new HashSet<string>();
            }
        }
        return index;
    }
}
