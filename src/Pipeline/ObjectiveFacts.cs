using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_Questrandomizer.Pipeline;

// THE "what is actually in effect" layer: pure static reads over (a quest, an objective outline).
//
// Every reader here answers the same question in the same shape - "what does this objective require
// RIGHT NOW" - by reading the outline's decision first and falling back to the quest when there is
// none. That `?? quest` fallback is the whole point: the outline is SPARSE (it stores decisions, not
// state), so anything the roll never touched simply isn't in it, and asking the outline alone gives
// you a null where the objective plainly has a requirement.
//
// TWO CALLERS, TWO SOURCE QUESTS - and both are correct:
//
//   [rolling]  QuestFacts wraps these and passes the VANILLA quest. The roll needs "what would this
//              objective require if I stopped deciding now", which is vanilla plus whatever this roll
//              has decided so far.
//   [text]     QuestLocaleGeneration calls these directly and passes the REGENERATED quest - the one
//              QuestRegeneration just built from this same outline. The sentence must describe what
//              the game will really check, and regeneration is the only thing that knows that: it
//              PURGES some vanilla fields the outline is silent about (kill Distance is rebuilt from
//              the outline every time, so a dropped distance really is gone) while PRESERVING others
//              (Daytime, SavageRole, BodyPart, pinned Weapon, Location). Reading pristine vanilla
//              there would describe requirements the player no longer has.
//
// For a decided field both sources give the same answer, so the split is only ever about which
// UNDECIDED values are still live - which is exactly the difference between those two quests.
//
// THE INVARIANT: static and pure. No config, no services, no per-roll state. Anything needing config
// or the quest graph (difficulty, blacklists, depth) stays in QuestFacts, which is a real service.
public static class ObjectiveFacts
{
    // ---- Kill objective: the values in effect ---------------------------------------------------

    // Boss/role hunt? The roller never DECIDES this (there is no savage-role roll - see QuestRoller),
    // so in practice it is always a vanilla shape fact; the outline side is read anyway because the
    // legacy-cache converter does write it, and because the "decision first" rule stays uniform.
    // When true it locks out location, target-swap, bodypart and time in the roll, and it makes the
    // role name(s) the rendered target in the text.
    public static bool HasSpecialTargetType(Quest source, ObjectiveOutline rolled)
    {
        List<string> roles = GetSavageRoles(source, rolled);
        return roles.Count > 0;
    }

    // The bot ROLES this objective hunts, else empty. See HasSpecialTargetType for why this is
    // almost always the quest's own value rather than a decision.
    public static List<string> GetSavageRoles(Quest source, ObjectiveOutline rolled)
    {
        List<string>? decided = rolled.Kills?.SavageRoles;
        if (decided != null)
        {
            return decided;
        }
        List<string>? fromQuest = KillsCondition(source, rolled.ConditionId)?.SavageRole;
        if (fromQuest != null)
        {
            return fromQuest;
        }
        return new List<string>();
    }

    // The target in effect: the one the roll decided, else the quest's.
    public static string? GetTarget(Quest source, ObjectiveOutline rolled)
    {
        return rolled.Kills?.Target ?? GetQuestTarget(source, rolled.ConditionId);
    }

    // The maps in effect for this objective: the ones the roll decided, else whatever the quest lists.
    public static List<string> GetMaps(Quest source, ObjectiveOutline rolled)
    {
        return rolled.Kills?.Maps ?? GetQuestMaps(source, rolled.ConditionId);
    }

    // Factory/Labs (and easy quests, handled by the caller) suppress the time and distance windows -
    // there is no long sightline or day/night cycle to gate on. Reads the maps decided so far.
    public static bool IsFactoryOrLab(Quest source, ObjectiveOutline rolled)
    {
        List<string> maps = GetMaps(source, rolled);
        foreach (string map in maps)
        {
            if (map is "factory4_day" or "factory4_night" or "laboratory")
            {
                return true;
            }
        }
        return false;
    }

    // The kill count in play: the roll's decided count, else the quest's raw count. (There is no
    // "seeding" - the `?? quest` IS how that count enters; nothing is pre-copied.)
    public static int? GetKillCount(Quest source, ObjectiveOutline rolled)
    {
        return rolled.Kills?.Count ?? GetQuestKillCount(source, rolled.ConditionId);
    }

    // Current kill DISTANCE value - the clear/reroll/add branch keys on whether it's > 0. Null = the
    // condition carries no distance at all (the whole distance step is then skipped).
    public static double? GetDistanceValue(Quest source, ObjectiveOutline rolled)
    {
        return rolled.Kills?.DistanceValue ?? KillsCondition(source, rolled.ConditionId)?.Distance?.Value;
    }

    // The comparison the distance uses (">=", "<=", ...), defaulting to ">=" when neither the roll nor
    // the quest names one - a distance with no compare method would render as a bare number.
    public static string GetDistanceCompare(Quest source, ObjectiveOutline rolled)
    {
        string? decided = rolled.Kills?.DistanceCompare;
        if (decided is { Length: > 0 })
        {
            return decided;
        }
        string? fromQuest = KillsCondition(source, rolled.ConditionId)?.Distance?.CompareMethod;
        if (fromQuest is { Length: > 0 })
        {
            return fromQuest;
        }
        return ">=";
    }

    // Current kill TIME-OF-DAY window (From, To). (0, 0) means "no window" - a genuinely absent
    // daytime and a present-but-unset one are treated identically, so there is nothing to gain from
    // telling them apart.
    public static (int From, int To) GetDaytime(Quest source, ObjectiveOutline rolled)
    {
        int? from = rolled.Kills?.DaytimeFrom;
        int? to = rolled.Kills?.DaytimeTo;
        if (from != null && to != null)
        {
            return (from.Value, to.Value);
        }
        DaytimeCounter? daytime = KillsCondition(source, rolled.ConditionId)?.Daytime;
        if (daytime == null)
        {
            return (0, 0);
        }
        return (daytime.From ?? 0, daytime.To ?? 0);
    }

    // Current kill BODY-PART requirement - the reroll-vs-add branch keys on whether it's non-empty.
    public static List<string> GetBodyParts(Quest source, ObjectiveOutline rolled)
    {
        return rolled.Kills?.BodyParts ?? KillsCondition(source, rolled.ConditionId)?.BodyPart ?? new List<string>();
    }

    // The weapon ids the objective pins DIRECTLY, i.e. the ones that have to be rendered as weapons
    // because no category name describes them. Three cases, in order:
    //   - the roll pinned a set (mystery arsenal / hand-authored)      -> those ids
    //   - the roll picked category NAMES                               -> empty: the caller renders the
    //     names instead, and the quest's Weapon list is just those categories expanded
    //   - the roll decided no weapons at all                           -> the quest's own weapon list
    public static List<string> GetPinnedWeapons(Quest source, ObjectiveOutline rolled)
    {
        var weapons = new List<string>();

        List<MongoId>? pinned = rolled.Kills?.PinnedWeapons;
        if (pinned is { Count: > 0 })
        {
            foreach (MongoId weapon in pinned)
            {
                weapons.Add(weapon.ToString());
            }
            return weapons;
        }

        if (rolled.Kills?.WeaponCategories is { Count: > 0 })
        {
            return weapons;
        }

        HashSet<string>? fromQuest = KillsCondition(source, rolled.ConditionId)?.Weapon;
        if (fromQuest != null)
        {
            foreach (string weapon in fromQuest)
            {
                weapons.Add(weapon);
            }
        }
        return weapons;
    }

    // ---- Handover objective: the values in effect -----------------------------------------------

    // The handover twin of GetKillCount - reads the Handover payload, and the count sits on the
    // task itself (not a sub-condition), so the fallback is the task's own Value.
    public static int? GetHandoverCount(Quest source, ObjectiveOutline rolled)
    {
        int? decidedCount = rolled.Handover?.Count;
        if (decidedCount != null)
        {
            return decidedCount;
        }
        double? questCount = Task(source, rolled.ConditionId)?.Value;
        if (questCount != null)
        {
            return (int)questCount.Value;
        }
        return null;
    }

    // Handover found-in-raid in effect. The roll only ever FORCES it true, so the fact is simply the
    // current value: the roll's if decided, else the quest's.
    public static bool GetFoundInRaid(Quest source, ObjectiveOutline rolled)
    {
        bool? decided = rolled.Handover?.FoundInRaid;
        if (decided != null)
        {
            return decided.Value;
        }
        return Task(source, rolled.ConditionId)?.OnlyFoundInRaid ?? false;
    }

    // The specific item this handover asks for, or null when it asks for a whole CATEGORY. A rolled
    // category is named in the outline and its item list only exists expanded on the built quest, so
    // there is no single item to name - the caller renders the category name instead.
    public static string? GetHandoverItem(Quest source, ObjectiveOutline rolled)
    {
        if (rolled.Handover?.CategoryName is { Length: > 0 })
        {
            return null;
        }
        List<MongoId>? decided = rolled.Handover?.ItemIds;
        if (decided is { Count: > 0 })
        {
            return decided[0].ToString();
        }
        return GetQuestHandoverItem(source, rolled.ConditionId);
    }

    // ---- Gear worn during the objective: read off the BUILT quest -------------------------------

    // Required and forbidden gear in effect. Unlike the readers above there is NO decision-first
    // fallback: the roll stores a gear GROUP NAME in the outline, and QuestRegeneration expands that
    // group into the concrete inclusive item list on the built quest - so the only place the actual
    // pieces exist is the quest, never the (sparse) outline. Located by the outline's own
    // GearConditionId: gear lives in its own counter condition, keyed separately from the objective's
    // main ConditionId, so this searches by that id rather than by an Equipment SubCondition lookup.
    // Null Required means "no required-gear line to render", matching how the rolled path signals it;
    // Forbidden drops empty groups (a forbidden-gear condition can carry placeholder empties).
    public static (List<List<string>>? Required, List<List<string>> Forbidden) GetGear(Quest source, ObjectiveOutline rolled)
    {
        var none = ((List<List<string>>?)null, new List<List<string>>());
        if (rolled.GearConditionId is not { } gearId)
        {
            return none;
        }
        List<QuestCondition> tasks = source.Conditions?.AvailableForFinish ?? new List<QuestCondition>();
        foreach (QuestCondition task in tasks)
        {
            List<QuestConditionCounterCondition> conditions = task.Counter?.Conditions ?? new List<QuestConditionCounterCondition>();
            foreach (QuestConditionCounterCondition condition in conditions)
            {
                if (condition.Id != gearId)
                {
                    continue;
                }
                var required = new List<List<string>>();
                if (condition.EquipmentInclusive != null)
                {
                    foreach (List<string> group in condition.EquipmentInclusive)
                    {
                        required.Add(group);
                    }
                }
                var forbidden = new List<List<string>>();
                if (condition.EquipmentExclusive != null)
                {
                    foreach (List<string> group in condition.EquipmentExclusive)
                    {
                        if (group.Count > 0)
                        {
                            forbidden.Add(group);
                        }
                    }
                }
                return (required.Count > 0 ? required : null, forbidden);
            }
        }
        return none;
    }

    // ---- Per-objective shape, read off the quest alone -------------------------------------------
    // Read-only glances located by the outline's pinned condition id, with no decision layered on -
    // for the questions that are about the quest's SHAPE ("does it have one of these at all") rather
    // than about a value in effect.

    // The raw kill count on the quest for this objective - the fallback source for GetKillCount.
    public static int? GetQuestKillCount(Quest source, MongoId conditionId)
    {
        double? value = Task(source, conditionId)?.Value;
        if (value != null)
        {
            return (int)value.Value;
        }
        return null;
    }

    // Is there a Location sub-condition here at all? Distinguishes "add a new location" from "reroll
    // the existing one". GetQuestMaps returns an empty list for BOTH "no condition" and "present but
    // empty", so it can't answer this on its own.
    public static bool HasQuestLocation(Quest source, MongoId conditionId)
    {
        return SubCondition(Task(source, conditionId), "Location") != null;
    }

    // Is there an Equipment sub-condition here at all (inclusive OR forbidden-only)? Gear-adding needs
    // to tell "no condition" (safe to add) from "a forbidden-gear-only condition" (leave alone) -
    // HasQuestIncludeGear can't, since a forbidden-only condition has no inclusive group either.
    public static bool HasQuestEquipment(Quest source, MongoId conditionId)
    {
        return SubCondition(Task(source, conditionId), "Equipment") != null;
    }

    public static string? GetQuestTarget(Quest source, MongoId conditionId)
    {
        return KillsCondition(source, conditionId)?.Target?.Item;
    }

    // The quest already pins a specific weapon set? Then the roll always re-rolls it (vs. a chance to
    // ADD one where there was none) - the weapon slot as it stands before any edit.
    public static bool HasQuestWeapons(Quest source, MongoId conditionId)
    {
        return KillsCondition(source, conditionId)?.Weapon is { Count: > 0 };
    }

    // The quest came with its own REQUIRED-gear list (a non-empty inclusive group)? That gates the
    // edit-existing-gear branch. Forbidden-gear-ONLY conditions have no inclusive group and are left
    // alone, so this must check the inclusive side specifically, not just "has an Equipment condition".
    public static bool HasQuestIncludeGear(Quest source, MongoId conditionId)
    {
        QuestConditionCounterCondition? gear = SubCondition(Task(source, conditionId), "Equipment");
        IEnumerable<List<string>>? groups = gear?.EquipmentInclusive;
        if (groups == null)
        {
            return false;
        }
        foreach (List<string> group in groups)
        {
            if (group.Count > 0)
            {
                return true;
            }
        }
        return false;
    }

    // The quest's map list for this objective (the Location sub-condition's targets), else empty. The
    // fallback source for GetMaps.
    public static List<string> GetQuestMaps(Quest source, MongoId conditionId)
    {
        QuestConditionCounterCondition? location = SubCondition(Task(source, conditionId), "Location");
        List<string>? maps = location?.Target?.List;
        if (maps != null)
        {
            return maps;
        }
        // No location sub-condition (or it carries no map list) -> no map restriction.
        return new List<string>();
    }

    // The item the quest's handover asks for (its first target) - what the roll picks a replacement for.
    public static string? GetQuestHandoverItem(Quest source, MongoId conditionId)
    {
        QuestCondition? task = Task(source, conditionId);
        List<string>? list = task?.Target?.List;
        if (list is { Count: > 0 })
        {
            return list[0];
        }
        return task?.Target?.Item;
    }

    // ---- Private structural navigation ----------------------------------------------------------

    private static QuestCondition? Task(Quest source, MongoId conditionId)
    {
        List<QuestCondition>? tasks = source.Conditions?.AvailableForFinish;
        if (tasks == null)
        {
            return null;
        }
        foreach (QuestCondition task in tasks)
        {
            if (task.Id == conditionId)
            {
                return task;
            }
        }
        return null;
    }

    private static QuestConditionCounterCondition? KillsCondition(Quest source, MongoId conditionId)
    {
        return SubCondition(Task(source, conditionId), "Kills");
    }

    private static QuestConditionCounterCondition? SubCondition(QuestCondition? task, string conditionType)
    {
        List<QuestConditionCounterCondition>? conditions = task?.Counter?.Conditions;
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
}
