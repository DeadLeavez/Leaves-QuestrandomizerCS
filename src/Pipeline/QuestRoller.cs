using System.Numerics;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using Leaves_Questrandomizer.Data;

namespace Leaves_Questrandomizer.Pipeline;

// STAGE 2 of the outline-native pipeline: the ROLL. Replaces the old QuestEditor + QuestTools.Randomize*
// (which mutated a scratch Quest and shuttled decisions through RandomizeFlags). Here each Decide*
// reads facts via QuestFacts and WRITES its decision straight into the outline being built - the outline
// is the only state that flows through the roll (see docs/rolling-decision-tree.md).
//
// Order mirrors docs/rolling-decision-tree.md, section "Order of decisions". A Decide* writes a field
// ONLY when it decides something; an objective that gets no decision is dropped at the end and served
// from vanilla (sparsity, no bookkeeping - the "kept?" test is a pure read of the record).
//
// WORK IN PROGRESS: this first slice implements the kill-objective distance step end to end to prove
// the shape. The other Decide* steps (location, gear, target, bodypart, time, weapon, count) and the
// whole handover + add-objective paths are stubbed below and land next.
public class QuestRoller(
    Leaves.TierList tierList,
    HandbookHelper handbookHelper)
{
    // force: the admin re-roll override, which force-rolls a quest the config would normally protect -
    // it bypasses the blacklist here the same way the old EditQuest force flag did.
    public QuestOutline RollQuest(Quest vanilla, bool force = false)
    {
        // Blacklisted quests are never edited - an empty outline records "seen, left vanilla".
        if (!force && QuestFacts.IsBlacklisted(vanilla.Id))
        {
            return new QuestOutline { QuestId = vanilla.Id };
        }

        // The vanilla objectives as id-only shells; Decide* fills their payloads.
        List<ObjectiveOutline> shells = QuestOutlineScaffold.Build(vanilla);

        DecideAddKillObjective(vanilla, shells);
        DecideAddHandoverObjective(vanilla, shells);

        foreach (ObjectiveOutline shell in shells)
        {
            if (shell.Type == ObjectiveType.Kills)
            {
                DecideKillObjective(vanilla, shell);
            }
            else if (shell.Type == ObjectiveType.Handover)
            {
                DecideHandoverObjective(vanilla, shell);
            }
        }

        // Keep a shell only if it is Added or actually got a decision; the rest are dropped and
        // regeneration serves them straight from vanilla. No "was it touched" flag - we read the record.
        List<ObjectiveOutline> kept = new List<ObjectiveOutline>();
        foreach (ObjectiveOutline shell in shells)
        {
            if (shell.Origin == ObjectiveOrigin.Added || HasAnyDecision(shell))
            {
                kept.Add(shell);
            }
        }

        return new QuestOutline { QuestId = vanilla.Id, Objectives = kept };
    }

    // ---- Add-objective (steps 1-2, quest-level) -------------------------------------------------

    // Step 1. Add a kill objective to a quest that has none (chance-gated; never when a Kills fail-state
    // would make it self-contradictory). The Added shell gets its base target + depth-scaled count here,
    // then flows through the per-objective kill Decide* steps like any other shell.
    private void DecideAddKillObjective(Quest vanilla, List<ObjectiveOutline> shells)
    {
        if (QuestFacts.HasAnyKills(vanilla) || QuestFacts.HasKillsFailstate(vanilla))
        {
            return;
        }
        if (Random.Shared.NextDouble() >= Settings.Config.AddKillObjectiveToQuestChance)
        {
            return;
        }

        double count = Settings.Config.AddKillObjectiveKillCount
            + QuestFacts.Depth(vanilla.Id) * Settings.Config.AddKillObjectiveDepthFactor
            + QuestFacts.Level(vanilla.Id) * Settings.Config.AddKillObjectiveLevelFactor;

        ObjectiveOutline added = new ObjectiveOutline
        {
            Origin = ObjectiveOrigin.Added,
            Type = ObjectiveType.Kills,
            ConditionId = new MongoId(),
            CounterId = new MongoId(),
            KillsConditionId = new MongoId(),
            Kills = new KillsOutline
            {
                Target = RollTargetEntry().Target,
                Count = (int)Math.Round(count)
            }
        };
        shells.Add(added);
    }

    // Step 2. Add a handover objective to a quest that has none (chance-gated). It has no vanilla item to
    // key off, so it rolls from a tier-5 fallback and is filled directly here; the per-objective handover
    // step no-ops on it (that step keys off a vanilla item this Added objective doesn't have).
    private void DecideAddHandoverObjective(Quest vanilla, List<ObjectiveOutline> shells)
    {
        if (QuestFacts.HasAnyHandover(vanilla))
        {
            return;
        }
        if (Random.Shared.NextDouble() >= Settings.Config.AddHandOverObjectiveToQuestChance)
        {
            return;
        }

        double count = Settings.Config.AddHandOverObjectiveBaseCount
            + QuestFacts.Depth(vanilla.Id) * Settings.Config.AddHandOverObjectiveDepthFactor
            + QuestFacts.Level(vanilla.Id) * Settings.Config.AddHandOverObjectiveLevelFactor;

        ObjectiveOutline added = new ObjectiveOutline
        {
            Origin = ObjectiveOrigin.Added,
            Type = ObjectiveType.Handover,
            ConditionId = new MongoId(),
            Handover = new HandoverOutline()
        };

        MongoId fallbackItem = tierList.getRandomItemFromTier(Settings.ItemTierList, 5);
        FillHandover(added.Handover, count, fallbackItem);
        shells.Add(added);
    }

    // ---- Kill objective (steps 3-11) ------------------------------------------------------------

    private void DecideKillObjective(Quest vanilla, ObjectiveOutline objective)
    {
        // Gate (step 3): a kill-failstate quest, or the edit-chance missing, leaves the objective as
        // vanilla. It stays payload-less and gets dropped by the "kept?" test above.
        if (QuestFacts.HasKillsFailstate(vanilla))
        {
            return;
        }
        if (Random.Shared.NextDouble() >= Settings.Config.ChanceToEditKillConditions)
        {
            return;
        }

        // We are editing this objective, so it needs a Kills payload to write decisions into.
        if (objective.Kills == null)
        {
            objective.Kills = new KillsOutline();
        }
        KillsOutline kills = objective.Kills;

        DecideLocation(vanilla, objective, kills);
        DecideGear(vanilla, objective, kills);
        DecideTarget(vanilla, objective, kills);
        DecideBodyPart(vanilla, objective, kills);
        DecideTime(vanilla, objective, kills);
        DecideDistance(vanilla, objective, kills);
        DecideWeapon(vanilla, objective, kills);
        DecideCount(vanilla, objective, kills);
    }

    // Step 6. Swaps who you kill to a weighted-random target - but never for a boss/role hunt, and only
    // when the current target is one we recognise. The swap's difficulty swing is read back by
    // DecideCount (via QuestFacts.TargetDifficultyDelta), so this MUST run before DecideCount.
    private void DecideTarget(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (QuestFacts.HasSpecialTargetType(vanilla, objective))
        {
            return;
        }
        string? current = QuestFacts.GetTarget(vanilla, objective);
        if (!QuestFacts.IsValidTarget(current))
        {
            return;
        }
        ValidTargetEntry chosen = RollTargetEntry();
        kills.Target = chosen.Target;
    }

    // Weighted pick over the configured targets (Usec/Bear are weight 0 and never land - see misc.jsonc).
    private ValidTargetEntry RollTargetEntry()
    {
        List<ValidTargetEntry> targets = Settings.Misc.ValidTargets;
        double totalWeight = 0;
        foreach (ValidTargetEntry entry in targets)
        {
            totalWeight += entry.Weight;
        }
        double roll = Random.Shared.NextDouble() * totalWeight;
        foreach (ValidTargetEntry entry in targets)
        {
            roll -= entry.Weight;
            if (roll < 0)
            {
                return entry;
            }
        }
        return targets[targets.Count - 1];
    }

    // Step 11. Scales the kill count by the target-difficulty swing, then applies the ± random
    // adjustment. Boss/role hunts are left unscaled. Reads the base count and the swing back off the
    // outline, so it MUST run after DecideTarget.
    private void DecideCount(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (QuestFacts.HasSpecialTargetType(vanilla, objective))
        {
            return;
        }
        int? baseCount = QuestFacts.GetKillCount(vanilla, objective);
        if (baseCount == null)
        {
            return;
        }
        double count = baseCount.Value;

        // A harder target (positive swing) needs fewer kills; an easier one needs more.
        int delta = QuestFacts.TargetDifficultyDelta(vanilla, objective);
        double factor = Settings.Config.KillCountWhenTargetTypeChangesFactor;
        if (delta > 0)
        {
            count = Math.Round(Math.Max(count / (delta * factor), 5));
        }
        else if (delta < 0)
        {
            count = Math.Round(count * (Math.Abs(delta) * factor));
        }

        // The ± random adjustment (config adjustKillCountFactorsUpDown), same helper the old roller used.
        Vector2 factors = new Vector2(
            (float)Settings.Config.AdjustKillCountFactorsUpDown[0],
            (float)Settings.Config.AdjustKillCountFactorsUpDown[1]);
        kills.Count = (int)Leaves.Utils.Instance.generateValueAdjustmentInt((int)count, factors);
    }

    // Step 9. Every kill objective is eligible: reroll an existing distance, or add one on a chance to
    // a quest that has none. (The old roller skipped conditions with no Distance field, but only to
    // avoid a null-ref while mutating it - regeneration builds the Distance from the outline, so there
    // is nothing to skip for here.)
    private void DecideDistance(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        double? originalDistance = QuestFacts.GetDistanceValue(vanilla, objective);

        // Factory, Labs and easy quests have no long sightlines to gate on. Clear an EXISTING distance
        // (stored 0 so it overrides vanilla's); if there was none, there is nothing to clear.
        if (QuestFacts.IsFactoryOrLab(vanilla, objective) || QuestFacts.IsEasyQuest(vanilla.Id))
        {
            if (originalDistance > 0)
            {
                kills.DistanceCompare = ">=";
                kills.DistanceValue = 0;
            }
            return;
        }

        // Already has a distance requirement -> reroll it.
        if (originalDistance > 0)
        {
            kills.DistanceCompare = RollDistanceCompare();
            kills.DistanceValue = RollDistanceValue();
            return;
        }

        // No distance requirement -> maybe add one. THIS is what gives distance to quests without it.
        if (Random.Shared.NextDouble() < Settings.Config.ChanceToAddDistance)
        {
            kills.DistanceCompare = RollDistanceCompare();
            kills.DistanceValue = RollDistanceValue();
        }
    }

    private static string RollDistanceCompare()
    {
        if (Random.Shared.Next(2) > 0)
        {
            return ">=";
        }
        return "<=";
    }

    private static double RollDistanceValue()
    {
        // 20, 30, 40 or 50 metres - matches the old roller's (rand(4) + 2) * 10.
        return (Random.Shared.Next(4) + 2) * 10;
    }

    // Step 4. Reroll the maps of an objective that already has a location, or add a location on a chance
    // to one that doesn't. Special-enemy hunts are left alone (those enemies are often map-locked).
    private void DecideLocation(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (QuestFacts.HasSpecialTargetType(vanilla, objective))
        {
            return;
        }
        if (QuestFacts.HasVanillaLocation(vanilla, objective.ConditionId))
        {
            kills.Maps = RollMaps(vanilla.Id);
            return;
        }
        if (Random.Shared.NextDouble() < Settings.Config.ChanceToAddLocations)
        {
            kills.Maps = RollMaps(vanilla.Id);
            // No vanilla Location sub-condition here, so mint the id regeneration will build it under.
            if (objective.LocationConditionId == null)
            {
                objective.LocationConditionId = new MongoId();
            }
        }
    }

    // A distinct random set of maps (LocationCount of them). Easy quests draw from the easier pool.
    private List<string> RollMaps(MongoId questId)
    {
        List<string> pool;
        if (QuestFacts.IsEasyQuest(questId))
        {
            pool = Settings.Misc.EasyMaps;
        }
        else
        {
            pool = Settings.Misc.ValidMaps;
        }
        string[] chosen = Leaves.Utils.Instance.getUniqueValues(pool.ToArray(), Settings.Config.LocationCount);
        return new List<string>(chosen);
    }

    // Step 7. Reroll an existing body-part requirement, or add one on a chance. Never for a special-enemy
    // hunt or an easy quest.
    private void DecideBodyPart(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (QuestFacts.HasSpecialTargetType(vanilla, objective) || QuestFacts.IsEasyQuest(vanilla.Id))
        {
            return;
        }

        List<string> current = QuestFacts.GetBodyParts(vanilla, objective);
        bool shouldSet;
        if (current.Count > 0)
        {
            shouldSet = true;
        }
        else
        {
            shouldSet = Random.Shared.NextDouble() < Settings.Config.ChanceToAddBodypart;
        }
        if (!shouldSet)
        {
            return;
        }

        List<List<string>> groups = Settings.Misc.BodyParts;
        kills.BodyParts = groups[Random.Shared.Next(groups.Count)];
    }

    // Step 8. On Factory/Labs (no day-night cycle) clear an existing window; otherwise reroll one, or add
    // one on a chance (never on easy quests). Special-enemy hunts keep their time.
    private void DecideTime(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (QuestFacts.HasSpecialTargetType(vanilla, objective))
        {
            return;
        }

        (int From, int To) current = QuestFacts.GetDaytime(vanilla, objective);
        bool hasWindow = current.From != 0 || current.To != 0;

        // No day-night cycle to gate on. Clear an EXISTING window (stored 0,0 to override vanilla's); if
        // there was none, there is nothing to clear.
        if (QuestFacts.IsFactoryOrLab(vanilla, objective))
        {
            if (hasWindow)
            {
                kills.DaytimeFrom = 0;
                kills.DaytimeTo = 0;
            }
            return;
        }

        if (hasWindow)
        {
            SetRandomWindow(kills);
            return;
        }
        if (!QuestFacts.IsEasyQuest(vanilla.Id) && Random.Shared.NextDouble() < Settings.Config.ChanceToAddTimeOfDay)
        {
            SetRandomWindow(kills);
        }
    }

    private void SetRandomWindow(KillsOutline kills)
    {
        int from = Random.Shared.Next(23);
        kills.DaytimeFrom = from;
        kills.DaytimeTo = (from + Settings.Config.TimeOfDayWindowHours) % 24;
    }

    // Step 5. Reroll an existing required-gear list, or add one to a quest with no gear condition.
    // Never on easy quests, and a FORBIDDEN-gear-only condition ("kill without wearing X") is left
    // alone - bolting a wear-requirement onto it would change what the quest is.
    private void DecideGear(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (QuestFacts.IsEasyQuest(vanilla.Id))
        {
            return;
        }
        // No gear groups defined => nothing to roll; leave the objective's gear as vanilla left it.
        if (Settings.GearCategoriesWeighting.Count == 0)
        {
            return;
        }
        if (QuestFacts.HasVanillaIncludeGear(vanilla, objective.ConditionId))
        {
            if (Random.Shared.NextDouble() < Settings.Config.ChanceToEditGear)
            {
                kills.RequiredGearCategory = RollGearCategory();
            }
            return;
        }
        if (QuestFacts.HasVanillaEquipment(vanilla, objective.ConditionId))
        {
            // Forbidden-gear-only - leave it be.
            return;
        }
        if (Random.Shared.NextDouble() < Settings.Config.ChanceToAddGear)
        {
            kills.RequiredGearCategory = RollGearCategory();
            // No vanilla Equipment sub-condition here, so mint the id regeneration will build it under.
            if (objective.GearConditionId == null)
            {
                objective.GearConditionId = new MongoId();
            }
        }
    }

    // One weighted gear group from gearcategories.jsonc. The group is late-bound: regeneration expands
    // it into "wear any one of these" against its live item list. Public so the outline-verification
    // pass can re-roll legacy specific-gear outlines into a group the same way a fresh roll would.
    public string RollGearCategory()
    {
        return RollWeightedCategory(Settings.GearCategoriesWeighting);
    }

    // Step 10. Decide whether the objective requires a weapon, then roll the requirement.
    private void DecideWeapon(Quest vanilla, ObjectiveOutline objective, KillsOutline kills)
    {
        if (!ShouldRequireWeapon(vanilla, objective))
        {
            return;
        }

        List<MongoId>? mystery = RollMysteryArsenal();
        if (mystery != null)
        {
            // A mystery arsenal replaces the category requirement entirely: the pinned set IS the
            // requirement, and the flag marks it so downstream code can recognise the case.
            kills.PinnedWeapons = mystery;
            kills.MysteryArsenal = true;
            return;
        }

        kills.WeaponCategories = RollWeaponRequirement();
    }

    // Rolls a mystery arsenal on a chance: a fixed set of specific weapons - one guaranteed pistol, one
    // guaranteed shotgun, plus MysteryArsenalWeaponCount random weapons drawn from the whole rollable
    // pool. Returns null when the roll misses, or when the Pistol/Shotgun categories are unavailable, so
    // the caller falls back to an ordinary category requirement.
    private List<MongoId>? RollMysteryArsenal()
    {
        if (Random.Shared.NextDouble() >= Settings.Config.ChanceForMysteryArsenal)
        {
            return null;
        }
        if (!Settings.ResolvedCategories.TryGetValue("Pistol", out var pistols)
            || !Settings.ResolvedCategories.TryGetValue("Shotgun", out var shotguns))
        {
            return null;
        }
        if (pistols.Weapons.Count == 0 || shotguns.Weapons.Count == 0)
        {
            return null;
        }

        // A set, so the guaranteed pistol/shotgun and the random picks never duplicate one another.
        HashSet<MongoId> arsenal = new HashSet<MongoId>();
        arsenal.Add(pistols.Weapons[Random.Shared.Next(pistols.Weapons.Count)]);
        arsenal.Add(shotguns.Weapons[Random.Shared.Next(shotguns.Weapons.Count)]);

        List<MongoId> pool = AllRollableWeapons();
        int wanted = arsenal.Count + Settings.Config.MysteryArsenalWeaponCount;
        // Cap the attempts: a small pool (or lots of duplicate draws) must not spin forever asking for
        // more distinct weapons than exist.
        int attempts = 0;
        int maxAttempts = (pool.Count + 1) * 4;
        while (arsenal.Count < wanted && arsenal.Count < pool.Count && attempts < maxAttempts)
        {
            arsenal.Add(pool[Random.Shared.Next(pool.Count)]);
            attempts++;
        }

        return new List<MongoId>(arsenal);
    }

    // The deduplicated union of every rollable category's weapons - the pool the random mystery-arsenal
    // picks draw from. AnyWeapon categories carry no explicit weapons, so they contribute nothing here.
    private List<MongoId> AllRollableWeapons()
    {
        HashSet<MongoId> pool = new HashSet<MongoId>();
        foreach (KeyValuePair<string, double> entry in Settings.WeaponCategoriesWeighting)
        {
            ResolvedWeaponCategory category = Settings.ResolvedCategories[entry.Key];
            foreach (MongoId weapon in category.Weapons)
            {
                pool.Add(weapon);
            }
        }
        return new List<MongoId>(pool);
    }

    // Rolls a weapon requirement: one weighted category (with its full mod requirements), or - on a
    // chance - two MOD-LESS categories unioned ("an AK or an SMG") with no mods. Public so the
    // legacy-cache converter can roll a fresh requirement the same way.
    public List<string> RollWeaponRequirement()
    {
        Dictionary<string, double> modless = ModlessWeighting();
        bool multi = modless.Count >= 2 && Random.Shared.NextDouble() < Settings.Config.ChanceForSecondWeaponCategory;
        if (multi)
        {
            string first = RollWeightedCategory(modless);
            modless.Remove(first);
            string second = RollWeightedCategory(modless);
            return new List<string> { first, second };
        }

        string single = RollWeightedCategory(Settings.WeaponCategoriesWeighting);
        return new List<string> { single };
    }

    // Require a weapon whenever vanilla already restricts one; otherwise a chance, but never forced onto
    // an easy quest that didn't already have one.
    private bool ShouldRequireWeapon(Quest vanilla, ObjectiveOutline objective)
    {
        if (QuestFacts.HasVanillaWeapons(vanilla, objective.ConditionId))
        {
            return true;
        }
        if (QuestFacts.IsEasyQuest(vanilla.Id))
        {
            return false;
        }
        return Random.Shared.NextDouble() < Settings.Config.ChanceToAddWeapon;
    }

    // The category weighting restricted to categories with NO required/forbidden mods of their own - the
    // only ones a multi-category roll may combine.
    private Dictionary<string, double> ModlessWeighting()
    {
        Dictionary<string, double> modless = new Dictionary<string, double>();
        foreach (KeyValuePair<string, double> entry in Settings.WeaponCategoriesWeighting)
        {
            ResolvedWeaponCategory category = Settings.ResolvedCategories[entry.Key];
            if (category.RequiredMods.Count == 0 && category.ForbiddenMods.Count == 0)
            {
                modless[entry.Key] = entry.Value;
            }
        }
        return modless;
    }

    // Weighted pick over a name -> weight map (cumulative walk). A weight of 0 never lands.
    private string RollWeightedCategory(Dictionary<string, double> weights)
    {
        double total = 0;
        foreach (KeyValuePair<string, double> entry in weights)
        {
            total += entry.Value;
        }
        double roll = Random.Shared.NextDouble() * total;
        string last = "";
        foreach (KeyValuePair<string, double> entry in weights)
        {
            last = entry.Key;
            roll -= entry.Value;
            if (roll < 0)
            {
                return entry.Key;
            }
        }
        return last;
    }

    // ---- Handover objective (steps 12-15) -------------------------------------------------------

    // Gate on a chance, then (if the item is editable) replace it with a tier-picked item or a whole
    // category, maybe force found-in-raid, and adjust the count. Mirrors the old RandomizeHandover, but
    // only DECIDES - it writes CategoryName (late-bound) or ItemIds (pinned), not the expanded item list.
    private void DecideHandoverObjective(Quest vanilla, ObjectiveOutline objective)
    {
        if (Random.Shared.NextDouble() >= Settings.Config.ChanceToEditHandoverCondition)
        {
            return;
        }
        string? originalItemStr = QuestFacts.GetVanillaHandoverItem(vanilla, objective.ConditionId);
        if (originalItemStr == null || !QuestFacts.IsHandoverEditable(vanilla, objective.ConditionId))
        {
            return;
        }
        MongoId originalItem = originalItemStr;

        if (objective.Handover == null)
        {
            objective.Handover = new HandoverOutline();
        }
        FillHandover(objective.Handover, QuestFacts.GetHandoverCount(vanilla, objective) ?? 0, originalItem);
    }

    // Fills a handover payload - item-vs-category, found-in-raid, adjusted count. Shared by the
    // edit-existing path and the add-new path (which passes a tier-5 fallback item, having no vanilla one).
    // Public because the outline-verification pass (OutlineVerification) re-rolls a broken handover
    // through it - a repair is just a fresh roll, and this is the one place that logic lives. Callers
    // outside a normal roll must blank the payload first (see FillHandover's branches: it only SETS the
    // field it rolls, never clears the other, so a stale opposite field would linger).
    public void FillHandover(HandoverOutline handover, double baseCount, MongoId originalItem, bool skipCount = false)
    {
        double count = baseCount;
        bool isCategory = Random.Shared.NextDouble() < Settings.Config.ChanceToRequireItemCategory;
        if (isCategory)
        {
            handover.CategoryName = RollHandoverCategory();
            if (!skipCount)
            {
                count *= Settings.Config.ItemCategoryMultiplier;
            }
        }
        else
        {
            handover.ItemIds = new List<MongoId> { RollTierItem(originalItem) };
        }

        // A chance to FORCE found-in-raid; otherwise the field stays null and regeneration keeps vanilla's.
        if (Random.Shared.NextDouble() < Settings.Config.ChanceHandoverNeedsFIR)
        {
            handover.FoundInRaid = true;
        }

        Vector2 factors = new Vector2(
            (float)Settings.Config.AdjustHandoverCountFactorsUpDown[0],
            (float)Settings.Config.AdjustHandoverCountFactorsUpDown[1]);
        handover.Count = (int)Leaves.Utils.Instance.generateValueAdjustmentInt((int)count, factors);
    }

    // A uniformly-random handover category name (regeneration expands it against live membership).
    private string RollHandoverCategory()
    {
        List<string> keys = new List<string>(Settings.HandoverCategories.Keys);
        return keys[Random.Shared.Next(keys.Count)];
    }

    // A random item from the tier the original item sits in - or, for an item with no known tier, the
    // tier its handbook price maps to.
    private MongoId RollTierItem(MongoId originalItem)
    {
        int tier = tierList.getTierFromID(Settings.ItemTierList, originalItem);
        if (tier == -1)
        {
            double cost = handbookHelper.GetTemplatePrice(originalItem);
            tier = tierList.getClosestTier(Settings.ItemTierList, (int)Math.Round(cost / Settings.Config.HandoverItemUnknownItemValueDivider));
        }
        return tierList.getRandomItemFromTier(Settings.ItemTierList, tier);
    }

    // ---- Finalize / sparsity --------------------------------------------------------------------

    private static bool HasAnyDecision(ObjectiveOutline objective)
    {
        if (objective.Kills != null && KillsHasDecision(objective.Kills))
        {
            return true;
        }
        if (objective.Handover != null && HandoverHasDecision(objective.Handover))
        {
            return true;
        }
        return false;
    }

    private static bool KillsHasDecision(KillsOutline kills)
    {
        return kills.Count != null
            || kills.Target != null
            || kills.SavageRoles != null
            || kills.BodyParts != null
            || kills.Maps != null
            || kills.DistanceCompare != null
            || kills.DistanceValue != null
            || kills.DaytimeFrom != null
            || kills.DaytimeTo != null
            || kills.WeaponCategories is { Count: > 0 }
            || kills.PinnedWeapons != null
            || kills.RequiredGear != null;
    }

    private static bool HandoverHasDecision(HandoverOutline handover)
    {
        return handover.CategoryName != null
            || handover.ItemIds != null
            || handover.Count != null
            || handover.FoundInRaid != null;
    }
}
