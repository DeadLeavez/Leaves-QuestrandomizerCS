using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_Questrandomizer.Pipeline;

// One-time migration: turns a profile's OLD cached quest (a fully-rolled quest from before outlines
// existed) into an outline, so it joins the pipeline without forcing a re-roll of the player's
// in-progress quests. The alternative was a full re-roll for everyone, disruptive on a coop server.
//
// The one field that cannot be read back out of a rolled quest is the weapon CATEGORY name - the roll
// long ago expanded it into a resolved id list, and reverse-matching that list to a category is fuzzy
// and drifts as membership changes. Rather than guess, a quest that HAD any weapon/mod requirement gets
// a freshly ROLLED one (see ConvertKills) - so it keeps its restricted-weapon character and gains a
// live-resolving category, just not necessarily the same one it had. Every other decision reads
// straight off the cached quest, so ids - and therefore the player's progress - are preserved exactly.
// Locked quests are handled elsewhere by simply re-rolling them fresh, so they never reach this
// converter; only engaged (accepted/completed) quests do.
public static class QuestOutlineConverter
{
    private const string SandboxHigh = "Sandbox_high";

    // vanillaConditionIds: the ids present on the ORIGINAL quest, used to tell a vanilla objective the
    // roll edited from one the roll ADDED (an added objective's id won't be among them).
    public static QuestOutline Convert(MongoId questId, Quest cachedQuest, HashSet<MongoId> vanillaConditionIds, QuestRoller roller)
    {
        List<ObjectiveOutline> objectives = new List<ObjectiveOutline>();

        foreach (QuestCondition task in cachedQuest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
        {
            ObjectiveOrigin origin = vanillaConditionIds.Contains(task.Id) ? ObjectiveOrigin.Vanilla : ObjectiveOrigin.Added;

            if (task.ConditionType == "CounterCreator" && task.Counter?.Conditions != null)
            {
                QuestConditionCounterCondition? kills = FindSubCondition(task.Counter.Conditions, "Kills");
                if (kills != null)
                {
                    objectives.Add(ConvertKills(task, kills, origin, roller));
                }
            }
            else if (task.ConditionType == "HandoverItem")
            {
                objectives.Add(ConvertHandover(task, origin));
            }
            // Everything else (FindItem, LeaveItemAtLocation, Skill, ...) this mod never touched, so it
            // has no decisions and belongs in no outline - regeneration re-reads it from vanilla.
        }

        return new QuestOutline { QuestId = questId, Objectives = objectives };
    }

    private static ObjectiveOutline ConvertKills(QuestCondition task, QuestConditionCounterCondition kills, ObjectiveOrigin origin, QuestRoller roller)
    {
        List<QuestConditionCounterCondition> conditions = task.Counter!.Conditions!;
        QuestConditionCounterCondition? location = FindSubCondition(conditions, "Location");
        QuestConditionCounterCondition? gear = FindSubCondition(conditions, "Equipment");

        bool savage = (kills.SavageRole?.Count ?? 0) > 0;

        // If the cached quest carried a weapon OR mod requirement, don't try to recover WHICH one (the
        // roll long ago expanded a category name into an id list, and reverse-matching is fuzzy) - just
        // ROLL A FRESH one through the roller, exactly as a new quest would (weighted category, maybe
        // two; no mystery arsenals). The quest keeps its "restricted-weapon" character, gains a
        // live-resolving category, and every condition id is preserved below, so progress is untouched.
        // Absent (no requirement in the cache) leaves it unrestricted.
        bool hadWeaponReq = (kills.Weapon?.Count ?? 0) > 0 || HasAnyWeaponMods(kills.WeaponModsInclusive);
        List<string>? freshCategories = hadWeaponReq ? roller.RollWeaponRequirement() : null;

        // The kill count round-trips as an int; the cached Value is a nullable double.
        int? killCount = null;
        if (task.Value != null)
        {
            killCount = (int)task.Value.Value;
        }

        KillsOutline killsMeta = new KillsOutline
        {
            Count = killCount,
            // Faction-specific PMC hunts (Usec/Bear) are disabled going forward (misc validTargets weight
            // 0), but a legacy cached quest may still pin one - fold it up to AnyPmc on migration so old
            // profiles lose it too. Count is left as the cached value: killing any PMC instead of a
            // specific faction is easier, not harder, so there's no need to inflate it.
            Target = savage ? null : MapDisabledTarget(kills.Target?.Item),
            SavageRoles = savage ? new List<string>(kills.SavageRole!) : null,
            BodyParts = kills.BodyPart is { Count: > 0 } ? new List<string>(kills.BodyPart) : null,
            Maps = StripDerivedMaps(location?.Target?.List),
            // Distance/daytime are stored as the FINAL cached values. Regeneration applies them
            // verbatim, so a "cleared" 0/">= 0" round-trips just as a real requirement does - no need
            // for the roll-time "was this processed" distinction the extractor carries.
            DistanceCompare = kills.Distance is { Value: > 0 } ? kills.Distance.CompareMethod : null,
            DistanceValue = kills.Distance is { Value: > 0 } ? kills.Distance.Value : null,
            // Widen a migrated time-of-day window by one hour EACH direction. Old quests were rolled
            // with a 6-hour window; the current default is 8 (config timeOfDayWindowHours), so +1/-1
            // brings an old 6h window up to 8h and lines it up with freshly-rolled quests.
            DaytimeFrom = HasDaytime(kills) ? WrapHour(kills.Daytime!.From!.Value - 1) : null,
            DaytimeTo = HasDaytime(kills) ? WrapHour(kills.Daytime!.To!.Value + 1) : null,
            // Gear stays - it is not a weapon/mod requirement. Read as the roller stored it (one piece
            // per group), so it round-trips through regeneration's one-per-group rebuild.
            RequiredGear = ExtractGear(gear),
            WeaponCategories = freshCategories
        };

        return new ObjectiveOutline
        {
            Origin = origin,
            Type = ObjectiveType.Kills,
            ConditionId = task.Id,
            CounterId = ParseId(task.Counter.Id),
            KillsConditionId = kills.Id,
            LocationConditionId = location?.Id,
            GearConditionId = gear?.Id,
            Kills = killsMeta
        };
    }

    private static ObjectiveOutline ConvertHandover(QuestCondition task, ObjectiveOrigin origin)
    {
        // Pin the required item list verbatim. If it was a category the list is large, but that is a
        // correct (if not late-binding) requirement - and handover items were never the thing the
        // strip is about.
        List<MongoId>? items = null;
        List<string>? rawList = task.Target?.List;
        if (rawList != null)
        {
            items = new List<MongoId>();
            foreach (string id in rawList)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    items.Add(new MongoId(id));
                }
            }
        }

        // The handover count round-trips as an int; the cached Value is a nullable double.
        int? handoverCount = null;
        if (task.Value != null)
        {
            handoverCount = (int)task.Value.Value;
        }

        return new ObjectiveOutline
        {
            Origin = origin,
            Type = ObjectiveType.Handover,
            ConditionId = task.Id,
            Handover = new HandoverOutline
            {
                ItemIds = items is { Count: > 0 } ? items : null,
                Count = handoverCount,
                FoundInRaid = task.OnlyFoundInRaid
            }
        };
    }

    private static List<MongoId>? ExtractGear(QuestConditionCounterCondition? gear)
    {
        IEnumerable<List<string>>? groups = gear?.EquipmentInclusive;
        if (groups == null)
        {
            return null;
        }

        List<MongoId> pieces = new List<MongoId>();
        foreach (List<string> group in groups)
        {
            if (group == null)
            {
                continue;
            }
            foreach (string piece in group)
            {
                if (!string.IsNullOrEmpty(piece))
                {
                    pieces.Add(new MongoId(piece));
                }
            }
        }

        if (pieces.Count > 0)
        {
            return pieces;
        }
        return null;
    }

    // Usec/Bear -> AnyPmc. Kept as a converter-only fold: regeneration and the roller resolve targets
    // live, so the moment a quest is (re)rolled it already can't land on Usec/Bear (weight 0); this only
    // exists to rewrite the frozen target a legacy cache baked in before that config existed.
    private static string? MapDisabledTarget(string? target)
    {
        return target is "Usec" or "Bear" ? "AnyPmc" : target;
    }

    // Keeps an hour value in 0-23, wrapping around midnight (-1 -> 23, 24 -> 0).
    private static int WrapHour(int hour)
    {
        return (hour + 24) % 24;
    }

    private static bool HasDaytime(QuestConditionCounterCondition kills)
    {
        return kills.Daytime != null && !(kills.Daytime.From is null or 0 && kills.Daytime.To is null or 0);
    }

    private static List<string>? StripDerivedMaps(List<string>? maps)
    {
        if (maps == null || maps.Count == 0)
        {
            return null;
        }

        List<string> kept = new List<string>();
        foreach (string map in maps)
        {
            if (!string.Equals(map, SandboxHigh, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(map);
            }
        }

        if (kept.Count > 0)
        {
            return kept;
        }
        return null;
    }

    private static MongoId? ParseId(string? raw)
    {
        return string.IsNullOrEmpty(raw) ? null : new MongoId(raw);
    }

    // The first sub-condition of the given type under a CounterCreator's Counter, or null if there is
    // none. Replaces a LINQ FirstOrDefault(predicate) - same behaviour, spelled out per house style.
    private static QuestConditionCounterCondition? FindSubCondition(List<QuestConditionCounterCondition>? conditions, string conditionType)
    {
        if (conditions == null)
        {
            return null;
        }
        foreach (QuestConditionCounterCondition condition in conditions)
        {
            if (condition.ConditionType == conditionType)
            {
                return condition;
            }
        }
        return null;
    }

    // True if the cached kills condition carried any weapon-mod requirement group at all. Replaces a
    // LINQ .Any() - EquipmentInclusive/WeaponModsInclusive are IEnumerable (no Count), so the emptiness
    // test is a foreach that returns on the first group.
    private static bool HasAnyWeaponMods(IEnumerable<List<string>>? groups)
    {
        if (groups == null)
        {
            return false;
        }
        foreach (List<string> group in groups)
        {
            return true;
        }
        return false;
    }
}
