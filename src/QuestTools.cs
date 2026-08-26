using System.Numerics;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils.Json;
using Leaves_Questrandomizer.Data;
// "LogLevel" alone is ambiguous - ASP.NET Core's own Microsoft.Extensions.Logging.LogLevel is
// implicitly in scope too (this project targets Microsoft.NET.Sdk.Web), and it has different
// members (Information, not Info).
using SptLogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace Leaves_Questrandomizer;

// How far into the vanilla quest chain a quest sits - depth = how many prerequisite quests come
// before it, level = the highest player-level requirement along the way to unlock it. Used to
// scale generated/added objectives (a quest deep into the chain gets a bigger kill count than an
// early one).
public class QuestDepth
{
    public int Depth { get; set; }
    public int Level { get; set; }
}

// C# equivalent of the TS original's LeavesQuestTools - the mechanical building blocks
// (add/randomize a kill objective, a handover objective, gear, weapon groups, etc). This class
// itself holds no per-profile state - DepthList is computed once from the vanilla quest graph
// (structural data, doesn't change per profile) and is safe to share read-only across every
// profile's concurrent generation.
//
// Uses Random.Shared (not `new Random()`) throughout - Random.Shared is specifically documented
// as safe to call from multiple threads at once, which plain Random instances are not. Since
// different profiles' quest generation could genuinely run concurrently under this project's new
// per-profile architecture (unlike the TS original's effectively-single-threaded world), that
// matters here in a way it didn't for the original.
public static class QuestTools
{
    private static Dictionary<MongoId, TemplateItem> itemDB = null!;

    // map -> zoneId -> (contents ignored, we only ever check whether the key exists).
    private static Dictionary<string, Dictionary<string, object>> questPoints = null!;

    private static readonly Dictionary<MongoId, QuestDepth> depthList = new();

    // Loads questpoints + builds the quest-depth graph once, from Questrandomizer.OnLoad (it logs
    // through Leaves.Utils.Instance). depthList is structural (the vanilla quest graph,
    // not per-profile) and read-only afterwards, so it is safe to read from concurrent per-profile
    // generation. TierList/HandbookHelper/QuestLocaleGeneration used to be constructor deps here but
    // were dead - nothing in this class used them - so they are simply gone.
    public static void Init(string modPath, Dictionary<MongoId, TemplateItem> items, Dictionary<MongoId, Quest> quests)
    {
        itemDB = items;

        questPoints = Leaves.Utils.Instance.loadFile<Dictionary<string, Dictionary<string, object>>>(System.IO.Path.Combine(modPath, "assets/data/questpoints.jsonc"));

        GenerateDepthList(quests);
        Leaves.Utils.Instance.printColor("[Questrandomizer] Generated depth list of all quests");
    }

    public static void ChangeXPOnQuest(Quest quest, double multiplier)
    {
        var successRewards = quest.Rewards?.GetValueOrDefault("Success");
        if (successRewards == null)
        {
            return;
        }
        foreach (var reward in successRewards)
        {
            if (reward.Type == RewardType.Experience && reward.Value != null)
            {
                reward.Value = reward.Value.Value * multiplier;
            }
        }
    }

    private static void GenerateDepthList(Dictionary<MongoId, Quest> quests)
    {
        foreach (var (questId, _) in quests)
        {
            var checkedQuests = new List<MongoId>();
            depthList[questId] = FindDepth(quests, questId, checkedQuests);
        }
    }

    private static QuestDepth FindDepth(Dictionary<MongoId, Quest> quests, MongoId questId, List<MongoId> checkedQuests)
    {
        checkedQuests.Add(questId);

        if (!quests.TryGetValue(questId, out var quest))
        {
            return new QuestDepth();
        }

        var currentDepth = new QuestDepth();
        foreach (var condition in quest.Conditions?.AvailableForStart ?? new List<QuestCondition>())
        {
            if (condition.ConditionType == "Quest")
            {
                string? targetStr = condition.Target?.Item;
                if (targetStr == null || !MongoId.IsValidMongoId(targetStr))
                {
                    continue;
                }
                MongoId target = targetStr;

                if (!quests.ContainsKey(target))
                {
                    Leaves.Utils.Instance.printColor(
                        $"Found quest with invalid data. Pre-req quest does not exist. Quest with issue: [{quest.QuestName}]-[{quest.Id}]. Required quest(missing): [{target}]",
                        LogTextColor.Red);
                    continue;
                }

                if (!depthList.ContainsKey(target))
                {
                    if (!checkedQuests.Contains(target))
                    {
                        depthList[target] = FindDepth(quests, target, checkedQuests);
                    }
                    else
                    {
                        // Found circular reference - this is an issue with modded quests, not
                        // something a vanilla install can produce.
                        Leaves.Utils.Instance.printColor(
                            $"Found circular quest reference. This is an issue with modded quests. :S QuestID:\"{quest.Id}\" - and name:\"{quest.QuestName}\"",
                            LogTextColor.Red);
                        return new QuestDepth();
                    }
                }

                int tempDepth = depthList[target].Depth + 1;
                int tempLevel = depthList[target].Level;
                if (currentDepth.Depth < tempDepth)
                {
                    currentDepth.Depth = tempDepth;
                }
                if (currentDepth.Level < tempLevel)
                {
                    currentDepth.Level = tempLevel;
                }
            }

            if (condition.ConditionType == "Level" && condition.Value != null)
            {
                if (currentDepth.Level < condition.Value.Value)
                {
                    currentDepth.Level = (int)condition.Value.Value;
                }
            }
        }
        return currentDepth;
    }

    public static QuestDepth? GetQuestDepth(MongoId questId)
    {
        return depthList.GetValueOrDefault(questId);
    }

    public static Dictionary<MongoId, QuestDepth> GetDepthList()
    {
        return depthList;
    }

    public static string ZoneIdToMap(string zoneId)
    {
        foreach (var (map, zones) in questPoints)
        {
            if (zones.ContainsKey(zoneId))
            {
                return map;
            }
        }
        return "unknown";
    }

    // The id parameters exist so REGENERATION can rebuild an appended objective carrying the very same
    // ids the roller minted for it - profile progress and locale text are keyed to them, so a rebuild
    // that re-minted would orphan both. Omitted (the roller's own call) = mint fresh, as before.
    // Sharing this one builder between rolling and regeneration also means the two can't drift apart.
    public static void AddKillObjectiveToQuest(Quest quest, string target, int count, bool useDepth = false,
        MongoId? pinnedObjectiveId = null, MongoId? pinnedKillsId = null, string? pinnedCounterId = null)
    {
        if (useDepth && !depthList.ContainsKey(quest.Id))
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer] Used DEPTH on a quest that doesn't have it. Quest:{quest.Id}", LogTextColor.Red);
        }
        if (useDepth && depthList.TryGetValue(quest.Id, out var qDepth))
        {
            double newCount = Settings.Config.AddKillObjectiveKillCount;
            newCount += qDepth.Depth * Settings.Config.AddKillObjectiveDepthFactor;
            newCount += qDepth.Level * Settings.Config.AddKillObjectiveLevelFactor;
            count = (int)Math.Round(newCount);
            Leaves.Utils.Instance.printColor($"[Questrandomizer]{quest.QuestName} Killcount = {count}", LogTextColor.Cyan, SptLogLevel.Info, true);
        }

        var killCondition = new QuestConditionCounterCondition
        {
            BodyPart = new List<string>(),
            CompareMethod = ">=",
            ConditionType = "Kills",
            Daytime = new DaytimeCounter { From = 0, To = 0 },
            Distance = new CounterConditionDistance { CompareMethod = ">=", Value = 0 },
            DynamicLocale = false,
            EnemyEquipmentExclusive = new List<List<string>>(),
            EnemyEquipmentInclusive = new List<List<string>>(),
            EnemyHealthEffects = new List<EnemyHealthEffect>(),
            Id = pinnedKillsId ?? new MongoId(),
            ResetOnSessionEnd = false,
            SavageRole = new List<string>(),
            Target = new ListOrT<string>(null, target),
            Value = 1,
            Weapon = new HashSet<string>(),
            WeaponCaliber = new List<string>(),
            WeaponModsExclusive = new List<List<string>>(),
            WeaponModsInclusive = new List<List<string>>()
        };

        var objectiveData = new QuestCondition
        {
            CompleteInSeconds = 0,
            ConditionType = "CounterCreator",
            Counter = new QuestConditionCounter
            {
                Conditions = new List<QuestConditionCounterCondition> { killCondition },
                Id = pinnedCounterId ?? new MongoId().ToString()
            },
            DoNotResetIfCounterCompleted = false,
            DynamicLocale = false,
            GlobalQuestCounterId = "",
            Id = pinnedObjectiveId ?? new MongoId(),
            Index = quest.Conditions!.AvailableForFinish!.Count,
            OneSessionOnly = false,
            ParentId = "",
            Type = "Elimination",
            Value = count,
            VisibilityConditions = new List<VisibilityCondition>()
        };

        quest.Conditions.AvailableForFinish.Add(objectiveData);
    }

    // Same pinning rationale as AddKillObjectiveToQuest above.
    public static int AddHandOverObjectiveToQuest(Quest quest, int count, List<string> items, bool fir = false, bool useDepth = false,
        MongoId? pinnedObjectiveId = null)
    {
        if (useDepth && !depthList.ContainsKey(quest.Id))
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer] Used DEPTH on a quest that doesn't have it. Quest:{quest.Id}", LogTextColor.Red);
        }
        if (useDepth && depthList.TryGetValue(quest.Id, out var qDepth))
        {
            double newCount = Settings.Config.AddHandOverObjectiveBaseCount;
            newCount += qDepth.Depth * Settings.Config.AddHandOverObjectiveDepthFactor;
            newCount += qDepth.Level * Settings.Config.AddHandOverObjectiveLevelFactor;
            count = (int)Math.Round(newCount);
            Leaves.Utils.Instance.printColor($"[Questrandomizer]{quest.QuestName} Handover count = {count}", LogTextColor.Cyan, SptLogLevel.Info, true);
        }

        var objectiveData = new QuestCondition
        {
            ConditionType = "HandoverItem",
            DogtagLevel = 0,
            DynamicLocale = false,
            GlobalQuestCounterId = "",
            Id = pinnedObjectiveId ?? new MongoId(),
            Index = quest.Conditions!.AvailableForFinish!.Count,
            IsEncoded = false,
            MaxDurability = 100,
            MinDurability = 0,
            OnlyFoundInRaid = fir,
            ParentId = "",
            Target = new ListOrT<string>(items, null),
            Value = count,
            VisibilityConditions = new List<VisibilityCondition>()
        };

        quest.Conditions.AvailableForFinish.Add(objectiveData);
        return quest.Conditions.AvailableForFinish.Count - 1;
    }

    public static string GetQuestLocationText(Quest quest)
    {
        var maps = new HashSet<string>();

        foreach (var task in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
        {
            if (!string.IsNullOrEmpty(task.ZoneId))
            {
                string temp = ZoneIdToMap(task.ZoneId);
                if (temp == "unknown")
                {
                    return "any";
                }
                maps.Add(temp);
            }

            if (task.Counter?.Conditions == null)
            {
                continue;
            }

            foreach (var condition in task.Counter.Conditions)
            {
                if (condition.Zones != null)
                {
                    foreach (var _ in condition.Zones)
                    {
                        // Faithful to the TS original: this re-checks task.ZoneId (already handled
                        // above, so almost always empty here) rather than the zone from this loop -
                        // looks like a copy-paste slip in the original that made this branch
                        // effectively dead, but left as-is since it's cosmetic (only affects the
                        // generated location description text, never actual quest logic) and I
                        // can't be certain it isn't intentional without knowing the original
                        // author's intent.
                        string temp = ZoneIdToMap(task.ZoneId ?? "");
                        if (temp == "unknown")
                        {
                            return "any";
                        }
                        maps.Add(temp);
                    }
                }

                if (condition.ConditionType == "Location")
                {
                    var targets = condition.Target?.List ?? (condition.Target?.Item != null ? new List<string> { condition.Target.Item } : new List<string>());
                    foreach (var map in targets)
                    {
                        maps.Add(map);
                    }
                }
            }
        }

        return maps.Count == 1 ? maps.First() : "any";
    }

    public static void PurgeFindItemTasks(List<QuestCondition> tasks)
    {
        tasks.RemoveAll(task =>
        {
            if (task.ConditionType != "FindItem")
            {
                return false;
            }

            var targets = task.Target?.List ?? (task.Target?.Item != null ? new List<string> { task.Target.Item } : new List<string>());
            bool purge = true;
            foreach (var item in targets)
            {
                if (itemDB.TryGetValue(item, out var template) && template.Properties?.QuestItem == true)
                {
                    purge = false;
                }
            }
            return purge;
        });
    }

    public static void PurgeOptionalTasks(List<QuestCondition> tasks)
    {
        // A task with NO parentId is always necessary - only tasks that explicitly have one (and
        // it's non-empty) are the "optional" ones. isNecessary looks like the sane way to check
        // this, but the TS original found it's always false/missing on real quest data, i.e.
        // useless - matches that observation rather than trusting isNecessary.
        tasks.RemoveAll(task => !string.IsNullOrEmpty(task.ParentId));
    }

    // pinnedId: when regeneration rebuilds a procedural quest's prerequisite link every boot, it must
    // reuse the same condition id rather than minting a new one each time, or the start-condition id
    // would churn on every restart. Fresh generation passes null and gets a new id, as before.
    public static void AddPrerequisiteQuest(Quest targetQuest, MongoId previousQuestId, HashSet<QuestStatusEnum> statuses, MongoId? pinnedId = null)
    {
        var prereqCondition = new QuestCondition
        {
            ConditionType = "Quest",
            Id = pinnedId ?? new MongoId(),
            Index = targetQuest.Conditions!.AvailableForStart!.Count,
            ParentId = "",
            DynamicLocale = false,
            Target = new ListOrT<string>(null, previousQuestId.ToString()),
            Status = statuses,
            GlobalQuestCounterId = "",
            AvailableAfter = 0,
            Dispersion = 0,
            VisibilityConditions = new List<VisibilityCondition>()
        };

        targetQuest.Conditions.AvailableForStart.Add(prereqCondition);
    }

    public static void AddPrerequisiteLevel(Quest targetQuest, int requiredLevel)
    {
        var prereqCondition = new QuestCondition
        {
            ConditionType = "Level",
            Id = new MongoId(),
            Index = targetQuest.Conditions!.AvailableForStart!.Count,
            ParentId = "",
            DynamicLocale = false,
            GlobalQuestCounterId = "",
            Value = requiredLevel,
            CompareMethod = ">=",
            VisibilityConditions = new List<VisibilityCondition>()
        };

        targetQuest.Conditions.AvailableForStart.Add(prereqCondition);
    }

    public static void AddRewardExperience(Quest targetQuest, double experienceCount, MongoId? pinnedId = null)
    {
        var reward = new Reward
        {
            AvailableInGameEditions = new HashSet<string>(),
            Value = experienceCount,
            Id = pinnedId ?? new MongoId(),
            Type = RewardType.Experience,
            Index = targetQuest.Rewards!["Success"].Count,
            Unknown = false
        };
        targetQuest.Rewards["Success"].Add(reward);
    }

    // DON'T give multiple of nonstackable items - matches the TS original's shouted warning
    // comment. This mod never has a reason to give more than 1 of a reward item, so it's not
    // enforced here, just preserved as a note for anyone adding a call site that might.
    public static void AddRewardItem(Quest targetQuest, MongoId itemId, int count, bool fir,
        MongoId? pinnedRewardId = null, MongoId? pinnedGroupId = null)
    {
        MongoId rewardGroupId = pinnedGroupId ?? new MongoId();
        var item = new Item
        {
            Id = rewardGroupId,
            Template = itemId,
            Upd = new Upd
            {
                StackObjectsCount = count,
                SpawnedInSession = fir
            }
        };

        var reward = new Reward
        {
            AvailableInGameEditions = new HashSet<string>(),
            Value = 1,
            Id = pinnedRewardId ?? new MongoId(),
            Type = RewardType.Item,
            Index = targetQuest.Rewards!["Success"].Count,
            Target = rewardGroupId.ToString(),
            Unknown = false,
            FindInRaid = true,
            Items = new List<Item> { item }
        };

        targetQuest.Rewards["Success"].Add(reward);
    }

    // Copies a resolved category onto a kill condition. All the interesting work (legacy
    // migration, the required-mods cartesian product, forbidden-mod flattening) happened ONCE at
    // load in Settings.CompileCategory - this just deep-copies the compiled shapes
    // so no two quests (or profiles) ever share mutable lists.
    public static void AddWeaponGroup(string group, QuestConditionCounterCondition condition)
    {
        var resolved = Settings.ResolvedCategories[group];
        condition.Weapon = new HashSet<string>(resolved.Weapons.Select(w => w.ToString()));
        condition.WeaponModsInclusive = resolved.CompiledRequiredGroups.Select(g => new List<string>(g)).ToList();
        condition.WeaponModsExclusive = resolved.CompiledForbiddenMods.Select(g => new List<string>(g)).ToList();
    }

}
