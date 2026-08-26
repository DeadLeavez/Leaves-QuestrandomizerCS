using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Leaves_Questrandomizer.Pipeline;

namespace Leaves_Questrandomizer;

// C# equivalent of the TS original's LeavesQuestGeneration - builds brand new quests from scratch
// (as opposed to QuestTools, which edits existing ones). Currently produces 3 chains: weapon
// mastery, sniper, and handover/fetch quests, all chained together into ONE combined prerequisite
// sequence (see Questrandomizer.GenerateProceduralQuests for why they share one "previousQuest"
// variable across all three loops, matching the TS original).
public class QuestGeneration(
    Leaves.TierList tierList,
    Leaves.IdStore generatedIds)
{
    public MongoId GetOrCreateQuestId(string name, int questNumber)
    {
        return generatedIds.GetOrCreate(name + questNumber);
    }

    public Quest GenerateEmptyQuest(string name, MongoId trader, string location, MongoId id)
    {
        return new Quest
        {
            QuestName = name,
            Id = id,
            CanShowNotificationsInGame = true,
            AcceptPlayerMessage = $"{id} acceptPlayerMessage",
            ChangeQuestMessageText = $"{id} changeQuestMessageText",
            CompletePlayerMessage = $"{id} completePlayerMessage",
            Conditions = new QuestConditionTypes
            {
                AvailableForFinish = new List<QuestCondition>(),
                AvailableForStart = new List<QuestCondition>(),
                Fail = new List<QuestCondition>()
            },
            Description = $"{id} description",
            FailMessageText = $"{id} failMessageText",
            DeclinePlayerMessage = $"{id} declinePlayerMessage",
            Name = $"{id} name",
            Note = $"{id} note",
            TraderId = trader,
            Location = location,
            Image = "/files/quest/icon/65899d03adeac0191c51e880.jpg",
            Type = QuestTypeEnum.Completion,
            IsKey = false,
            Restartable = false,
            InstantComplete = false,
            SecretQuest = false,
            StartedMessageText = $"{id} startedMessageText",
            SuccessMessageText = $"{id} successMessageText",
            Rewards = new Dictionary<string, List<Reward>>
            {
                ["Started"] = new List<Reward>(),
                ["Success"] = new List<Reward>(),
                ["Fail"] = new List<Reward>()
            },
            Side = "Pmc"
        };
    }

    public void SetBaseQuestLocale(
        LocaleWorkspace workspace,
        MongoId id,
        string acceptPlayerMessage,
        string changeQuestMessageText,
        string completePlayerMessage,
        string description,
        string failMessageText,
        string declinePlayerMessage,
        string startedMessageText,
        string successMessageText,
        string questName)
    {
        workspace.Edit($"{id} acceptPlayerMessage", acceptPlayerMessage);
        workspace.Edit($"{id} changeQuestMessageText", changeQuestMessageText);
        workspace.Edit($"{id} completePlayerMessage", completePlayerMessage);
        workspace.Edit($"{id} description", description);
        workspace.Edit($"{id} failMessageText", failMessageText);
        workspace.Edit($"{id} declinePlayerMessage", declinePlayerMessage);
        workspace.Edit($"{id} startedMessageText", startedMessageText);
        workspace.Edit($"{id} successMessageText", successMessageText);
        // Write the plain name. The "(M) " mod marker is applied later, in ONE place for every
        // non-vanilla quest (ours and other mods' alike) - see Questrandomizer.MarkModdedQuests,
        // which reads this workspace edit back and prefixes it.
        workspace.Edit($"{id} name", questName);
    }

    // Picks one random reward item from the tier appropriate for questNumber (common/rare/epic
    // thresholds from QuestGen_RewardPools). Shared by AddRewardsToQuest and RerollRewards.
    private MongoId PickRewardItem(int questNumber)
    {
        var rewardTiers = Settings.Config.QuestGenRewardPools;
        var rewardTable = Settings.Misc.Rewards;

        string selectedTier = "common";
        if (questNumber >= rewardTiers.Common)
        {
            selectedTier = "common";
        }
        if (questNumber >= rewardTiers.Rare)
        {
            selectedTier = "rare";
        }
        if (questNumber >= rewardTiers.Epic)
        {
            selectedTier = "epic";
        }

        var pool = selectedTier switch
        {
            "rare" => rewardTable.Rare,
            "epic" => rewardTable.Epic,
            _ => rewardTable.Common
        };
        return pool[Random.Shared.Next(pool.Count)];
    }

    // Builds the RNG-FREE base of a procedural quest from its header - the stand-in for the vanilla DB
    // row a generated quest never had, and the procedural counterpart of "fetch the vanilla quest" that
    // regeneration does for edited quests. Everything here is derived or pinned, no die is cast: the
    // empty quest shell, the XP formula, the pinned reward item, and the prerequisite link (rebuilt
    // from ChainAfter with the pinned condition id so it does not churn each boot). The kill/handover
    // OBJECTIVE is deliberately NOT added here - QuestRegeneration.Regenerate recreates it from the
    // outline's Added objectives, exactly as it does for an edited quest. Locale text is a separate
    // pass. Usage: build this, then hand it to Regenerate(quest, outline).
    public Quest BuildProceduralSkeleton(MongoId questId, ProceduralHeader header)
    {
        var quest = GenerateEmptyQuest(header.Slot, header.Trader, ELocationName.any.ToString(), questId);

        // Order mirrors GenerateKillQuest exactly (link, then XP, then item) so the reward Index values
        // and condition ordering line up field-for-field with a freshly generated quest.
        if (header.ChainAfter is { } previous)
        {
            QuestTools.AddPrerequisiteQuest(quest, previous, new HashSet<QuestStatusEnum> { QuestStatusEnum.Success }, header.LinkConditionId);
        }

        QuestTools.AddRewardExperience(quest, Settings.Config.QuestGenXPPerQuest * header.Index, header.RewardXpId);
        if (header.RewardItem is { } rewardItem)
        {
            QuestTools.AddRewardItem(quest, rewardItem, 1, true, header.RewardItemId, header.RewardItemGroupId);
        }

        return quest;
    }

    // Reads back the single item reward AddRewardItem put in the Success bucket (the reward's own tpl,
    // not the wrapper item id). Null if there somehow isn't one, so the header records absence honestly
    // rather than a default id.
    private static MongoId? ExtractRewardItem(Quest quest)
    {
        var itemReward = quest.Rewards?["Success"].FirstOrDefault(r => r.Type == RewardType.Item);
        return itemReward?.Items?.FirstOrDefault()?.Template;
    }

    // ---- OUTLINE-NATIVE procedural authoring (the new pipeline) ---------------------------------
    //
    // These replaced the old GenerateKillQuest / GenerateHandoverQuest finished-quest authors (removed
    // with the pre-outline pipeline): instead of building a finished quest and reading it back with the
    // old extractor, they write the QuestOutline directly. The roll
    // happens HERE (the reward item, and for the caller the weapon requirement); everything the player's
    // progress hangs off - the objective condition ids and the prerequisite link id - is minted here and
    // pinned in the outline so a rebuild reuses it. The RNG-free rebuild is BuildProceduralSkeleton +
    // QuestRegeneration.Regenerate + the locale passes, identical to a stored outline on any later boot.

    // The base identity of a procedural quest. Everything derivable (name/description/XP) stays out of
    // the outline; only the pinned reward item, the trader, the chain position and the (minted) link id
    // are stored. Mirrors what AddPrerequisiteQuest + AddRewardsToQuest would have produced.
    private ProceduralHeader BuildProceduralHeader(string slot, int questNumber, MongoId trader, MongoId previousQuest)
    {
        return new ProceduralHeader
        {
            Slot = slot,
            Index = questNumber,
            Trader = trader,
            ChainAfter = questNumber != 0 ? previousQuest : (MongoId?)null,
            LinkConditionId = questNumber != 0 ? new MongoId() : (MongoId?)null,
            RewardItem = PickRewardItem(questNumber),
            // The reward ids are minted once here and pinned so the skeleton rebuild is byte-identical
            // every boot (see ProceduralHeader) - the item reward always exists, so all three are set.
            RewardXpId = new MongoId(),
            RewardItemId = new MongoId(),
            RewardItemGroupId = new MongoId()
        };
    }

    // Author a procedural KILL quest as an outline. The weapon requirement is rolled by the caller (via
    // QuestRoller.RollWeaponRequirement, the same roll vanilla kill quests use) and passed in, so this
    // method casts no weapon die itself. `distance` marks a sniper quest, whose range is a fixed formula.
    public QuestOutline AuthorKillQuestOutline(
        string slot,
        MongoId previousQuest,
        MongoId trader,
        int questNumber,
        int killCount,
        List<string> weaponCategories,
        bool distance)
    {
        MongoId questId = GetOrCreateQuestId(slot, questNumber);

        KillsOutline kills = new KillsOutline
        {
            Count = killCount,
            Target = "Any",
            WeaponCategories = weaponCategories.Count > 0 ? weaponCategories : null
        };
        if (distance)
        {
            kills.DistanceCompare = ">=";
            kills.DistanceValue = Math.Min(200, 50 + questNumber * 10);
        }

        ObjectiveOutline objective = new ObjectiveOutline
        {
            Origin = ObjectiveOrigin.Added,
            Type = ObjectiveType.Kills,
            // The three ids AddKillObjectiveToQuest would have minted, pinned so regeneration rebuilds
            // the CounterCreator task, its Counter, and the Kills sub-condition under the same ids.
            ConditionId = new MongoId(),
            CounterId = new MongoId(),
            KillsConditionId = new MongoId(),
            Kills = kills
        };

        return new QuestOutline
        {
            QuestId = questId,
            Procedural = BuildProceduralHeader(slot, questNumber, trader, previousQuest),
            Objectives = new List<ObjectiveOutline> { objective }
        };
    }

    // Author a procedural HANDOVER quest as an outline. The item and found-in-raid flag are rolled here
    // (a die over a tier pool, so the item is pinned - re-resolving it could change what the player must
    // bring mid-quest), matching the old GenerateHandoverQuest exactly (count 5, tier-capped item).
    public QuestOutline AuthorHandoverQuestOutline(
        string slot,
        MongoId previousQuest,
        MongoId trader,
        int questNumber)
    {
        MongoId questId = GetOrCreateQuestId(slot, questNumber);

        int tier = tierList.getClosestTier(Settings.ItemTierList, questNumber);
        int maxTier = Settings.Config.QuestGenHandoverMaxTier;
        if (tier > maxTier)
        {
            tier = tierList.getClosestTier(Settings.ItemTierList, maxTier);
        }
        MongoId itemToFind = tierList.getRandomItemFromTier(Settings.ItemTierList, tier);

        bool needsFoundInRaid = Random.Shared.NextDouble() < Settings.Config.QuestGenHandoverFIRChance;

        HandoverOutline handover = new HandoverOutline
        {
            Count = 5,
            ItemIds = new List<MongoId> { itemToFind },
            FoundInRaid = needsFoundInRaid
        };

        ObjectiveOutline objective = new ObjectiveOutline
        {
            Origin = ObjectiveOrigin.Added,
            Type = ObjectiveType.Handover,
            ConditionId = new MongoId(),
            Handover = handover
        };

        return new QuestOutline
        {
            QuestId = questId,
            Procedural = BuildProceduralHeader(slot, questNumber, trader, previousQuest),
            Objectives = new List<ObjectiveOutline> { objective }
        };
    }

    // The base quest locale (name/description/messages) for a procedural quest, keyed off its slot +
    // index. Deterministic - no die - so it runs on EVERY boot, for a freshly authored outline and a
    // stored one alike, exactly the way GenerateFromOutline regenerates the objective sentence. That is
    // what lets a wording fix in the QG_* locale keys reach every existing procedural quest.
    public void GenerateProceduralBaseLocale(LocaleWorkspace workspace, ProceduralHeader header, MongoId questId)
    {
        // The QG_* quest flavor text is English-only content (kept in assets/data/localization/en/
        // base.json, same as map and enemy names). Resolved once and written flat to the workspace;
        // GetLocaleDbPatch overlays it onto whatever language a client asks for (no blanks).
        string reportThis = QuestLocaleGeneration.GetLoc("QG_ReportThis");
        string description = QuestLocaleGeneration.GetLoc($"{header.Slot}Description");
        string accept = QuestLocaleGeneration.GetLoc($"{header.Slot}Accept");
        string success = QuestLocaleGeneration.GetLoc($"{header.Slot}Success");
        string name = QuestLocaleGeneration.GetLoc($"{header.Slot}Name") + (header.Index + 1);
        SetBaseQuestLocale(
            workspace,
            questId,
            reportThis + 1,
            reportThis + 2,
            reportThis + 3,
            description,
            reportThis + 4,
            reportThis + 5,
            accept,
            success,
            name);
    }

}
