using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer;

// Mirrors config/config.jsonc exactly (field-for-field, including the TS original's inconsistent
// capitalization like "addKillOBjectiveDepthFactor" - kept as-is so the shipped config.jsonc,
// which real users already have opinions/edits on, doesn't need renaming).
public record QuestrandomizerConfig
{
    [JsonPropertyName("questBlacklist")]
    public required List<MongoId> QuestBlacklist { get; set; }

    [JsonPropertyName("chanceToEditKillConditions")]
    public required double ChanceToEditKillConditions { get; set; }

    [JsonPropertyName("chanceToAddLocations")]
    public required double ChanceToAddLocations { get; set; }

    [JsonPropertyName("locationCount")]
    public required int LocationCount { get; set; }

    // NOT required (default true) so a config that predates this field still loads. Factory day and
    // night are separate locations to the backend but one map to the player. When on, an objective
    // fixed to Factory - Day is widened at regeneration to ALSO count at night (never the reverse: a
    // night-only objective stays night-only). Widened objectives read simply "[Factory]".
    [JsonPropertyName("factoryDayQuestsAlsoCountAtNight")]
    public bool FactoryDayQuestsAlsoCountAtNight { get; set; } = true;

    [JsonPropertyName("chanceToAddTimeOfDay")]
    public required double ChanceToAddTimeOfDay { get; set; }

    // NOT required (default 8) so a config that predates this field still loads. The length in hours of
    // a rolled time-of-day window (kills only count between From and From+this).
    [JsonPropertyName("timeOfDayWindowHours")]
    public int TimeOfDayWindowHours { get; set; } = 8;

    [JsonPropertyName("chanceToAddBodypart")]
    public required double ChanceToAddBodypart { get; set; }

    [JsonPropertyName("chanceToAddDistance")]
    public required double ChanceToAddDistance { get; set; }

    [JsonPropertyName("chanceToAddWeapon")]
    public required double ChanceToAddWeapon { get; set; }

    // NOT required (default 0.15) so older configs still load. Chance a rolled weapon requirement uses
    // TWO categories ("an AK or an SMG") instead of one. A multi-category roll only combines categories
    // that have no mod requirements, so it is weapons-only.
    [JsonPropertyName("chanceForSecondWeaponCategory")]
    public double ChanceForSecondWeaponCategory { get; set; } = 0.15;

    // NOT required (defaults so older configs still load). Chance a rolled weapon requirement is a
    // "mystery arsenal" instead of a category: a fixed set of specific weapons - one guaranteed pistol,
    // one guaranteed shotgun, plus MysteryArsenalWeaponCount random weapons from the whole pool.
    [JsonPropertyName("chanceForMysteryArsenal")]
    public double ChanceForMysteryArsenal { get; set; } = 0.10;

    [JsonPropertyName("mysteryArsenalWeaponCount")]
    public int MysteryArsenalWeaponCount { get; set; } = 3;

    [JsonPropertyName("chanceToAddGear")]
    public required double ChanceToAddGear { get; set; }

    // NOT `required` like its neighbors: this key was added after release-shaped configs already
    // existed, and a required property would make deserialization throw (crashing OnLoad) for
    // anyone whose config.jsonc predates it. Default matches the shipped config.jsonc value.
    [JsonPropertyName("chanceToEditGear")]
    public double ChanceToEditGear { get; set; } = 0.3;

    // NO LONGER USED: gear is now rolled as a single weighted GROUP (gearcategories.jsonc), so there is
    // no "how many pieces" knob any more. Kept (and made non-required) so existing config.jsonc files
    // that still carry the key deserialize without a crash; safe to delete from a config.
    [JsonPropertyName("addGearCount")]
    public int AddGearCount { get; set; } = 1;

    // [down, up] - e.g. [0.20, 0.20] means the random multiplier ranges 0.8-1.2.
    [JsonPropertyName("adjustKillCountFactorsUpDown")]
    public required List<double> AdjustKillCountFactorsUpDown { get; set; }

    [JsonPropertyName("killCountWhenTargetTypeChangesFactor")]
    public required double KillCountWhenTargetTypeChangesFactor { get; set; }

    [JsonPropertyName("addKillObjectiveToQuestChance")]
    public required double AddKillObjectiveToQuestChance { get; set; }

    [JsonPropertyName("addKillObjectiveKillCount")]
    public required int AddKillObjectiveKillCount { get; set; }

    [JsonPropertyName("addKillOBjectiveDepthFactor")]
    public required double AddKillObjectiveDepthFactor { get; set; }

    [JsonPropertyName("addKillOBjectiveLevelFactor")]
    public required double AddKillObjectiveLevelFactor { get; set; }

    [JsonPropertyName("addHandOverObjectiveToQuestChance")]
    public required double AddHandOverObjectiveToQuestChance { get; set; }

    [JsonPropertyName("addHandOverObjectiveBaseCount")]
    public required int AddHandOverObjectiveBaseCount { get; set; }

    [JsonPropertyName("addHandOverObjectiveDepthFactor")]
    public required double AddHandOverObjectiveDepthFactor { get; set; }

    [JsonPropertyName("addHandOverObjectiveLevelFactor")]
    public required double AddHandOverObjectiveLevelFactor { get; set; }

    [JsonPropertyName("chanceToEditHandoverCondition")]
    public required double ChanceToEditHandoverCondition { get; set; }

    [JsonPropertyName("chanceToRequireItemCategory")]
    public required double ChanceToRequireItemCategory { get; set; }

    [JsonPropertyName("itemCategoryMultiplier")]
    public required double ItemCategoryMultiplier { get; set; }

    [JsonPropertyName("handoverItemUnknownItemValueDivider")]
    public required double HandoverItemUnknownItemValueDivider { get; set; }

    [JsonPropertyName("adjustHandoverCountFactorsUpDown")]
    public required List<double> AdjustHandoverCountFactorsUpDown { get; set; }

    [JsonPropertyName("chanceHandoverNeedsFIR")]
    public required double ChanceHandoverNeedsFIR { get; set; }

    [JsonPropertyName("rainbowifyFIR")]
    public required bool RainbowifyFIR { get; set; }

    [JsonPropertyName("handoverItemBlacklist")]
    public required List<MongoId> HandoverItemBlacklist { get; set; }

    [JsonPropertyName("enableQuestWhilelist")]
    public required bool EnableQuestWhitelist { get; set; }

    [JsonPropertyName("questXPMultiplier")]
    public required double QuestXPMultiplier { get; set; }

    // Diagnostic only. When true, every rolled quest is ALSO regenerated from the outline captured
    // from that same roll and the two are compared - proving stage 3 of the outline pipeline
    // reproduces the roller exactly. Not "required": an existing config.jsonc without the key simply
    // leaves it off, and it costs nothing when off.
    [JsonPropertyName("pipelineSelfTest")]
    public bool PipelineSelfTest { get; set; }

    // Diagnostic only (not required, off by default). When true, at boot the NEW outline-native roller
    // is run over every vanilla quest and each result is regenerated (twice, to prove determinism) and
    // localized; a summary of failures + decision distributions + sample lines is logged.
    [JsonPropertyName("rollerSelfTest")]
    public bool RollerSelfTest { get; set; }

    [JsonPropertyName("enableTraderWhitelist")]
    public required bool EnableTraderWhitelist { get; set; }

    [JsonPropertyName("traderWhitelist")]
    public required List<MongoId> TraderWhitelist { get; set; }

    [JsonPropertyName("easierQuestList")]
    public required List<MongoId> EasierQuestList { get; set; }

    [JsonPropertyName("QuestGen_TotalWeaponQuests")]
    public required int QuestGenTotalWeaponQuests { get; set; }

    [JsonPropertyName("QuestGen_TotalHandoverQuests")]
    public required int QuestGenTotalHandoverQuests { get; set; }

    [JsonPropertyName("QuestGen_HandoverFIRChance")]
    public required double QuestGenHandoverFIRChance { get; set; }

    [JsonPropertyName("QuestGen_HandoverMaxTier")]
    public required int QuestGenHandoverMaxTier { get; set; }

    [JsonPropertyName("QuestGen_TotalSniperQuests")]
    public required int QuestGenTotalSniperQuests { get; set; }

    [JsonPropertyName("QuestGen_SniperWeaponGroup")]
    public required string QuestGenSniperWeaponGroup { get; set; }

    // Both "not implemented" per the TS original's own comment - parsed for config-shape parity,
    // not used yet.
    [JsonPropertyName("QuestGen_TotalRunnerQuests")]
    public required int QuestGenTotalRunnerQuests { get; set; }

    [JsonPropertyName("QuestGen_TotalPostmanQuests")]
    public required int QuestGenTotalPostmanQuests { get; set; }

    [JsonPropertyName("QuestGen_Trader")]
    public required MongoId QuestGenTrader { get; set; }

    [JsonPropertyName("QuestGen_XPPerQuest")]
    public required double QuestGenXPPerQuest { get; set; }

    [JsonPropertyName("QuestGen_RewardPools")]
    public required QuestGenRewardPools QuestGenRewardPools { get; set; }

    // The web UI profile filter (WebUI_FilterProfiles / WebUI_HiddenProfilePrefixes) moved to LeavesUtils'
    // config.jsonc - one shared filter for every mod's web UI. ShouldHideProfileFromWebUi delegates there.

    [JsonPropertyName("timeBetweenUnloadChecks")]
    public required int TimeBetweenUnloadChecks { get; set; }

    [JsonPropertyName("allowAllProfilesToDoAllQuests")]
    public required bool AllowAllProfilesToDoAllQuests { get; set; }

    [JsonPropertyName("DebugEnabled")]
    public required bool DebugEnabled { get; set; }

    [JsonPropertyName("purgeQuests")]
    public required List<MongoId> PurgeQuests { get; set; }

    // Value is either another profile id, or the literal string "original" - not a MongoId, so
    // this stays a plain string dictionary rather than Dictionary<MongoId, MongoId>.
    [JsonPropertyName("profileTranslator")]
    public required Dictionary<string, string> ProfileTranslator { get; set; }
}

public record QuestGenRewardPools
{
    [JsonPropertyName("common")]
    public required int Common { get; set; }

    [JsonPropertyName("rare")]
    public required int Rare { get; set; }

    [JsonPropertyName("epic")]
    public required int Epic { get; set; }
}
