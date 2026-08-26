using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils.Json;
using SptLogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace Leaves_Questrandomizer.Pipeline;

// STAGE 3 of the pipeline: outline + the ORIGINAL vanilla quest -> a real quest the game understands.
//
// This is the only stage that runs on every server start, and the whole runtime path is
// "file -> here". Extraction and rolling do not participate: they ran once, when the quest was rolled.
//
// It is deliberately, provably RNG-FREE. Every choice was already made and written down; this stage
// only applies them. If anything here rolled a die, quests would quietly mutate on every restart.
//
// Because it is handed the vanilla quest, everything the outline does NOT mention keeps coming from
// vanilla, fresh, every boot - original locale ids, quest linking and prerequisites, rewards, and
// every objective this mod never touched. Those therefore track upstream: if SPT fixes a broken
// vanilla objective, existing profiles pick the fix up instead of being frozen at roll-day state.
//
// And because the outline stores weapon requirements as a category NAME rather than a resolved id
// list, the resolution below reads live membership - so a weapon added by a content mod, or one that
// newly matches a caliber selector, becomes usable for every existing quest with no re-roll at all.
// That is the entire point of the exercise.
public class QuestRegeneration
{
    private const string Sandbox = "Sandbox";
    private const string SandboxHigh = "Sandbox_high";
    private const string FactoryDay = "factory4_day";
    private const string FactoryNight = "factory4_night";

    // Applies an outline to a vanilla quest IN PLACE (the caller owns cloning it, exactly as the old
    // edit path did). The step order mirrors the roller's on purpose - appended objectives first, then
    // the per-task pass, then the purges, then the location text - because the purges and the
    // visibility wipe must see the appended objectives too.
    // STRUCTURE ONLY. Objective TEXT is a separate pass over the same outline
    // (QuestLocaleGeneration.GenerateFromOutline), so no LocaleWorkspace is threaded through any of
    // this - the two are independent consumers of the decisions rather than one job carrying the
    // other's accumulator down four levels.
    public void Regenerate(Quest quest, QuestOutline outline)
    {
        var availableForFinish = quest.Conditions?.AvailableForFinish;
        if (availableForFinish == null)
        {
            return;
        }

        // Deliberately NOT an outline field: the XP multiplier is a config value applied with no die
        // rolled, so it is a deterministic transform, not a decision. Doing it here rather than baking
        // it in is also a straight upgrade on the old cache, where a later questXPMultiplier change
        // could never reach an already-rolled quest - now it lands on every quest at the next restart.
        // Idempotent because regeneration always starts from a fresh vanilla clone, so the multiplier
        // is applied to the ORIGINAL xp value each time and never compounds.
        QuestTools.ChangeXPOnQuest(quest, Settings.Config.QuestXPMultiplier);

        var byId = outline.Objectives.ToDictionary(objective => objective.ConditionId);

        // Whether this quest is one we actually changed. Empty means the roller decided nothing here
        // (blacklisted / whitelisted-out / no facet rolled), so we leave it byte-for-byte vanilla.
        bool questWasModified = outline.Objectives.Count > 0;

        // Objectives the roller APPENDED don't exist in the vanilla quest, so recreate them first -
        // before the per-task pass, so they also get the visibility wipe and the purges, exactly as
        // they did when rolled. They are rebuilt through the same QuestTools builders the roller uses,
        // handed the ids it minted back then, so shape and identity both survive the round trip.
        // Depth-based counts are deliberately skipped (useDepth: false): the final count is a stored
        // decision and is applied from the outline in the per-task pass below.
        foreach (var objective in outline.Objectives.Where(o => o.Origin == ObjectiveOrigin.Added))
        {
            if (objective.Type == ObjectiveType.Kills && objective.Kills is { } killDecisions)
            {
                QuestTools.AddKillObjectiveToQuest(
                    quest, killDecisions.Target ?? "Any", killDecisions.Count ?? 1, false,
                    objective.ConditionId, objective.KillsConditionId, objective.CounterId?.ToString());
            }
            else if (objective.Type == ObjectiveType.Handover && objective.Handover is { } handoverDecisions)
            {
                var items = handoverDecisions.ItemIds?.Select(item => item.ToString()).ToList() ?? new List<string>();
                QuestTools.AddHandOverObjectiveToQuest(
                    quest, handoverDecisions.Count ?? 1, items, handoverDecisions.FoundInRaid ?? false, false,
                    objective.ConditionId);
            }
        }

        foreach (var task in availableForFinish)
        {
            // Strip the one-session flag off every condition of a quest we modified. Vanilla marks a
            // handful of quests one-session ("visit these places AND extract alive, all in ONE raid" -
            // Scout, The Bunker, Nothing Fishy About This...). Once we ADD a kill/handover objective (or
            // rewrite a kill onto a different map) those conditions can no longer all be satisfied in a
            // single raid, so the client's one-session gate never opens - the quest shows every objective
            // done but offers no Complete button, and the one-session counters reset to 0 at each raid end
            // (which also makes the web UI, reading live counters, show them incomplete). Clearing the
            // flag makes each condition persist once met, so the quest completes piecemeal across raids -
            // exactly what the 3.x TS version did. Deterministic transform (like the XP multiplier above),
            // reapplied every boot over a fresh vanilla clone, so it never compounds. Untouched quests
            // (questWasModified == false) keep their vanilla one-session behaviour.
            if (questWasModified)
            {
                task.OneSessionOnly = false;
            }

            // Applies to EVERY CounterCreator, decisions or not: the roller purges these in its scan
            // loop, before it decides whether to edit anything. An objective with no Kills sub-condition
            // (ExitStatus + InZone, say) is still purged, so this cannot live inside ApplyKills.
            if (task.ConditionType == "CounterCreator" && task.Counter?.Conditions != null)
            {
                task.Counter.Conditions.RemoveAll(condition => condition.ConditionType is "InZone" or "HealthEffect");
            }

            // No mode filter here: an appended objective exists by now, so it takes its decisions the
            // same way an edited vanilla one does.
            if (byId.TryGetValue(task.Id, out var objective))
            {
                if (task.ConditionType == "CounterCreator" && objective is { Type: ObjectiveType.Kills, Kills: not null })
                {
                    ApplyKills(task, objective);
                }
                else if (task.ConditionType == "HandoverItem" && objective is { Type: ObjectiveType.Handover, Handover: not null })
                {
                    ApplyHandover(task, objective.Handover);
                }
            }

            task.VisibilityConditions = new List<VisibilityCondition>();
        }

        QuestTools.PurgeFindItemTasks(availableForFinish);
        QuestTools.PurgeOptionalTasks(availableForFinish);

        string locationText = QuestTools.GetQuestLocationText(quest).ToLowerInvariant();
        if (Settings.Misc.LocationIdMap.TryGetValue(locationText, out var locationId))
        {
            quest.Location = locationId;
        }
    }

    private void ApplyKills(QuestCondition task, ObjectiveOutline objective)
    {
        var conditions = task.Counter?.Conditions;
        if (conditions == null)
        {
            return;
        }

        var kills = FindOrCreate(conditions, objective.KillsConditionId, "Kills");
        if (kills == null)
        {
            return;
        }

        var killsOutline = objective.Kills!;

        // Always reset the mod lists before applying: they are rebuilt wholesale below, and leaving
        // vanilla's entries in place would compound them every rebuild.
        kills.WeaponModsExclusive = new List<List<string>>();
        kills.WeaponModsInclusive = new List<List<string>>();
        kills.EnemyHealthEffects = new List<EnemyHealthEffect>();

        if (killsOutline.Target is { Length: > 0 } target)
        {
            kills.Target = new ListOrT<string>(null, target);
        }
        if (killsOutline.SavageRoles is { Count: > 0 } savageRoles)
        {
            kills.SavageRole = new List<string>(savageRoles);
        }
        if (killsOutline.BodyParts is { Count: > 0 } bodyParts)
        {
            kills.BodyPart = new List<string>(bodyParts);
        }
        //If distance is not in the outline, we purge it.
        kills.Distance = new CounterConditionDistance
        {
            CompareMethod = killsOutline.DistanceCompare is { Length: > 0 } compare ? compare : ">=",
            Value = killsOutline.DistanceValue is { } distance ? distance : 0
        };
        if (killsOutline.DaytimeFrom is { } from && killsOutline.DaytimeTo is { } to)
        {
            kills.Daytime = new DaytimeCounter { From = from, To = to };
        }

        if (killsOutline.Count is { } count)
        {
            task.Value = count;
        }

        ApplyWeapons(kills, killsOutline);
        ApplyMaps(conditions, objective, killsOutline);
        StripSecondaryEquipment(conditions, objective.GearConditionId);
        ApplyGear(conditions, objective, killsOutline);
    }

    // BSG authored a handful of quests (e.g. "The Good Times - Part 1") with MORE THAN ONE Equipment
    // sub-condition on a single CounterCreator - a separate "wearing" requirement per piece (body armor
    // in one, helmet in the next) instead of the usual single Equipment condition holding several
    // groups. This mod's model has exactly ONE gear condition per objective (the pinned GearConditionId,
    // which the scaffold reads as the FIRST Equipment), so ApplyGear and the locale only ever touch that
    // one - any extra Equipment condition survives regeneration as a LIVE requirement the player must
    // satisfy but never sees asked for. Strip those extras so the built quest matches what the locale
    // shows. Runs only for a mod-managed kill objective (this is called from ApplyKills), so a quest
    // left vanilla keeps its vanilla shape and its vanilla text together.
    private static void StripSecondaryEquipment(List<QuestConditionCounterCondition> conditions, MongoId? primaryGearId)
    {
        // The Equipment condition to KEEP: the pinned one when present, else the first Equipment (so an
        // objective with no gear decision still collapses to a single, rendered requirement).
        MongoId? keepId = null;
        if (primaryGearId is { } pinned)
        {
            foreach (QuestConditionCounterCondition condition in conditions)
            {
                if (condition.ConditionType == "Equipment" && condition.Id == pinned)
                {
                    keepId = pinned;
                    break;
                }
            }
        }
        if (keepId == null)
        {
            foreach (QuestConditionCounterCondition condition in conditions)
            {
                if (condition.ConditionType == "Equipment")
                {
                    keepId = condition.Id;
                    break;
                }
            }
        }
        if (keepId == null)
        {
            return;
        }

        // Back-to-front so removals don't disturb the indices still to be checked.
        for (int index = conditions.Count - 1; index >= 0; index--)
        {
            QuestConditionCounterCondition condition = conditions[index];
            if (condition.ConditionType == "Equipment" && condition.Id != keepId)
            {
                conditions.RemoveAt(index);
            }
        }
    }

    private void ApplyHandover(QuestCondition task, HandoverOutline decisions)
    {
        if (decisions.CategoryName is { Length: > 0 } category
            && Settings.HandoverCategories.TryGetValue(category, out var categoryItems))
        {
            // Late-bound, same as a weapon category: expanded from live membership every build.
            task.Target = new ListOrT<string>(categoryItems.Select(item => item.ToString()).ToList(), null);
        }
        else if (decisions.ItemIds is { Count: > 0 } items)
        {
            task.Target = new ListOrT<string>(items.Select(item => item.ToString()).ToList(), null);
        }
        else if (decisions.CategoryName is { Length: > 0 } missing)
        {
            // Category was renamed or removed from config since the roll - leave vanilla's requirement
            // rather than emitting an objective with nothing that can satisfy it.
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Regeneration: handover category '{missing}' no longer exists - leaving the vanilla requirement.",
                LogTextColor.Red);
        }

        if (decisions.Count is { } count)
        {
            task.Value = count;
        }
        task.OnlyFoundInRaid = decisions.FoundInRaid;

        // Deterministic, so not stored: the roller always flattens these.
        task.MaxDurability = 100;
        task.MinDurability = 0;
        task.VisibilityConditions = new List<VisibilityCondition>();
    }

    // THE late-binding step. The stored name is resolved against live category membership, so the
    // weapon list and the category's compiled required/forbidden mod groups all reflect whatever is
    // installed RIGHT NOW - not what was installed on the day the quest was rolled.
    private void ApplyWeapons(QuestConditionCounterCondition kills, KillsOutline decisions)
    {
        if (decisions.WeaponCategories is { Count: > 0 } categories)
        {
            ApplyWeaponCategories(kills, categories);
            return;
        }

        if (decisions.PinnedWeapons is { Count: > 0 } mystery)
        {
            // A mystery arsenal is a rolled SET of specific weapons, not a named category, so there is
            // nothing to late-bind to - the pinned ids are the requirement.
            kills.Weapon = new HashSet<string>(mystery.Select(id => id.ToString()));
        }
    }

    // The multi-category form. A single category behaves exactly like the legacy path (its weapons + mod
    // requirements). Two or more union the weapons ONLY - a multi-roll only ever picks mod-less
    // categories, so there are no mod requirements, and the mod lists ApplyKills already cleared stay empty.
    private void ApplyWeaponCategories(QuestConditionCounterCondition kills, List<string> categories)
    {
        if (categories.Count == 1)
        {
            string category = categories[0];
            if (!Settings.ResolvedCategories.ContainsKey(category))
            {
                Leaves.Utils.Instance.printColor(
                    $"[Questrandomizer] Regeneration: weapon category '{category}' no longer exists - "
                    + "leaving this objective's vanilla weapon requirement in place.",
                    LogTextColor.Red);
                return;
            }
            QuestTools.AddWeaponGroup(category, kills);
            return;
        }

        HashSet<string> union = new HashSet<string>();
        foreach (string category in categories)
        {
            if (Settings.ResolvedCategories.TryGetValue(category, out var resolved))
            {
                foreach (MongoId weapon in resolved.Weapons)
                {
                    union.Add(weapon.ToString());
                }
            }
        }
        kills.Weapon = union;
    }

    private void ApplyMaps(List<QuestConditionCounterCondition> conditions, ObjectiveOutline objective, KillsOutline decisions)
    {
        if (decisions.Maps is not { Count: > 0 } maps)
        {
            return;
        }
        var location = FindOrCreate(conditions, objective.LocationConditionId, "Location");
        if (location == null)
        {
            return;
        }

        // Ground Zero is one map to the player but two to the backend. The outline stores only what
        // was chosen; re-expanding is deterministic, so it belongs here rather than in the file.
        var expanded = new List<string>(maps);
        if (expanded.Any(map => string.Equals(map, Sandbox, StringComparison.OrdinalIgnoreCase))
            && !expanded.Any(map => string.Equals(map, SandboxHigh, StringComparison.OrdinalIgnoreCase)))
        {
            expanded.Add(SandboxHigh);
        }

        // Factory day and night are separate locations to the backend but one map to the player. A
        // day-fixed objective should ALSO count at night (never the reverse - a night-only objective
        // stays night-only). Deterministic, config-gated, and re-expanded here for the same reason as
        // Ground Zero above: the outline stores only the chosen map. The display side collapses the
        // day+night pair back to a single "[Factory]".
        if (Settings.Config.FactoryDayQuestsAlsoCountAtNight
            && expanded.Any(map => string.Equals(map, FactoryDay, StringComparison.OrdinalIgnoreCase))
            && !expanded.Any(map => string.Equals(map, FactoryNight, StringComparison.OrdinalIgnoreCase)))
        {
            expanded.Add(FactoryNight);
        }
        location.Target = new ListOrT<string>(expanded, null);
    }

    private void ApplyGear(List<QuestConditionCounterCondition> conditions, ObjectiveOutline objective, KillsOutline decisions)
    {
        // Resolve the pieces the player may wear. Preferred path: a named gear GROUP, expanded here
        // against its live item list (late-bound - an item added to the group reaches this quest with
        // no re-roll). Fallback: the legacy pinned RequiredGear id set, kept working until the
        // outline-verification pass converts it to a group. A group set wins if both somehow exist.
        var pieces = new List<string>();
        if (decisions.RequiredGearCategory is { } groupName
            && Settings.GearCategories.TryGetValue(groupName, out var group))
        {
            foreach (MongoId item in group.Items)
            {
                pieces.Add(item.ToString());
            }
        }
        else if (decisions.RequiredGear is { Count: > 0 } gear)
        {
            foreach (MongoId item in gear)
            {
                pieces.Add(item.ToString());
            }
        }

        if (pieces.Count == 0)
        {
            return;
        }
        var equipment = FindOrCreate(conditions, objective.GearConditionId, "Equipment");
        if (equipment == null)
        {
            return;
        }
        // One single-item group per piece - "wear any one of these".
        var groups = new List<List<string>>();
        foreach (string piece in pieces)
        {
            groups.Add(new List<string> { piece });
        }
        equipment.EquipmentInclusive = groups;
    }

    // Find-or-create by pinned id. A sub-condition that came from vanilla is found; one this mod
    // minted at roll time isn't present in the vanilla quest, so it is created carrying the very same
    // id it was given then. That single rule covers both origins - and because the id never changes,
    // the player's progress and the objective's locale text stay attached across every rebuild.
    private static QuestConditionCounterCondition? FindOrCreate(
        List<QuestConditionCounterCondition> conditions, MongoId? pinnedId, string conditionType)
    {
        if (pinnedId is not { } id)
        {
            return null;
        }
        var existing = conditions.FirstOrDefault(condition => condition.Id == id);
        if (existing != null)
        {
            return existing;
        }

        var created = new QuestConditionCounterCondition
        {
            Id = id,
            ConditionType = conditionType,
            DynamicLocale = false
        };
        if (conditionType == "Location")
        {
            created.Target = new ListOrT<string>(new List<string>(), null);
        }
        if (conditionType == "Equipment")
        {
            created.EquipmentInclusive = new List<List<string>>();
            created.EquipmentExclusive = new List<List<string>>();
            // An Equipment condition must set this explicitly; a created condition must match the rolled
            // one exactly or the rebuilt quest differs from it by a single stray field.
            created.IncludeNotEquippedItems = false;
        }
        conditions.Add(created);
        return created;
    }

}
