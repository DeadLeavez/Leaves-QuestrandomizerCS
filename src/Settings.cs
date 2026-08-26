using Leaves_Questrandomizer.Data;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Services;
using SptLogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace Leaves_Questrandomizer;

// C# equivalent of the TS original's LeavesSettingsManager - loads every config/data file ONCE
// and exposes them as read-only static properties. Deliberately NOT a place for any per-profile
// mutable state (the TS original kept a "localizationChanges" scratch dictionary here that got reused
// and reset per profile generation - that's fine in TS's effectively-single-threaded world, but here,
// with real concurrent requests possible, ANY mutable field shared across profiles is a bug
// waiting to happen. Per-profile scratch state gets threaded explicitly through method
// parameters instead - see the locale generation code).
//
// STATIC by design, not [DI-Injectable]: it holds the mod's OWN config/data, loaded from disk the
// same way every other mod in this workspace loads its config.jsonc - nothing DI needs to construct.
// Making it static is what lets every hand-built helper read Settings.Config / Settings.Misc / ...
// directly instead of taking a `settings` constructor parameter threaded down the whole pipeline.
//
// TWO-PHASE, and the phases are load-bearing:
//   - Init() (from Questrandomizer.OnLoad) loads every file. Reads Leaves.Utils.Instance, which is
//     self-registered when DI constructs LeavesUtils - already available this early, no ordering to mind.
//   - The Apply*/Compile*/Resolve*/Finalize* pass (from LateOnLoad, PostSptModLoader) fills the
//     category data once EVERY mod's weapons exist. This is the ONLY window anything here mutates;
//     after it, every property is read-only and safe to read from concurrent per-profile generation
//     without locking. Do not add a mutator that runs later than LateOnLoad.
public static class Settings
{
    public static QuestrandomizerConfig Config { get; private set; } = null!;

    // Raw file data - kept for ModCategories (name -> mod ids). Weapon
    // categories themselves should be read through ResolvedCategories below, never through
    // WeaponCategories.Categories.
    public static WeaponCategoriesData WeaponCategories { get; private set; } = null!;

    // Every weapon category, migrated (if the file used the legacy format) and compiled into
    // the shapes its consumers need - see ResolvedWeaponCategory.cs.
    public static Dictionary<string, ResolvedWeaponCategory> ResolvedCategories { get; private set; } = null!;

    // Precomputed once from the resolved categories' weights - this is the exact shape
    // WeightedRandomHelper.GetWeightedValue expects, so it doesn't need recomputing on every pick.
    public static Dictionary<string, double> WeaponCategoriesWeighting { get; private set; } = null!;

    // Fully expanded "category name -> every item id belonging to it" - combines the
    // parent-category whitelist scan and the hand-picked custom categories from
    // handovercategories.jsonc into one lookup (see BuildHandoverCategories below).
    public static Dictionary<string, List<MongoId>> HandoverCategories { get; private set; } = null!;

    // Item ids that must never be asked for in a handover - a raw passthrough of
    // handovercategories.jsonc's ItemBlacklist. Read by the outline-verification pass, which re-rolls
    // any stored handover that landed on one. A HashSet so the per-objective membership test is O(1).
    public static HashSet<MongoId> HandoverItemBlacklist { get; private set; } = null!;

    public static MiscData Misc { get; private set; } = null!;

    // Gear groups the "wear X" objectives roll over: group name -> its interchangeable pieces + weight.
    // Replaces the old flat gearlist.jsonc: a quest now rolls ONE weighted group and asks for any one
    // of its items (late-bound like weapon categories - adding an item to a group reaches every quest
    // that rolled it, with no re-roll). GearCategoriesWeighting is the name -> weight map the roller
    // picks over; GearCategories is the full group data regeneration expands into requirements.
    public static Dictionary<string, GearCategoryEntry> GearCategories { get; private set; } = null!;
    public static Dictionary<string, double> GearCategoriesWeighting { get; private set; } = null!;

    public static List<MongoId> QuestWhitelist { get; private set; } = null!;
    public static Dictionary<int, List<MongoId>> ItemTierList { get; private set; } = null!;
    public static List<WeaponEquivalentEntry> WeaponEquivalents { get; private set; } = null!;

    // category name -> extra weapon ids, merged from every compat file (see
    // WeaponCompatibilityFile.CategoryAdditions). Applied at the final load stage by
    // ApplyCategoryAdditions, once every mod's weapons actually exist.
    public static Dictionary<string, List<MongoId>> CategoryAdditions { get; private set; } = null!;

    // mod-category name -> extra MOD ids (suppressors, bipods, optics...), merged from every compat
    // file. Applied by ApplyModCategoryAdditions BEFORE compilation - see there for why the order
    // matters.
    public static Dictionary<string, List<MongoId>> ModCategoryAdditions { get; private set; } = null!;

    // category name -> roll weight, merged from every compat file's weaponCategoryWeights (largest
    // wins on conflict). Only consulted by ApplyCategoryAdditions when it CREATES a category the
    // compat files invented; existing categories keep their weaponcategories.jsonc weight.
    public static Dictionary<string, double> CategoryWeights { get; private set; } = null!;

    public static void Init(string modPath, ModHelper modHelper, DatabaseService databaseService, Leaves.TierList tierList)
    {
        Config = modHelper.GetJsonDataFromFile<QuestrandomizerConfig>(modPath, "config/config.jsonc");
        WeaponCategories = modHelper.GetJsonDataFromFile<WeaponCategoriesData>(modPath, "assets/data/weaponcategories.jsonc");
        Misc = modHelper.GetJsonDataFromFile<MiscData>(modPath, "assets/data/misc.jsonc");
        var gearCategoriesFile = modHelper.GetJsonDataFromFile<GearCategoriesData>(modPath, "assets/data/gearcategories.jsonc");
        GearCategories = gearCategoriesFile.GearGroups;
        GearCategoriesWeighting = new Dictionary<string, double>();
        foreach (var (groupName, group) in GearCategories)
        {
            // A group with no items can never satisfy a wear-requirement, so keep it out of the roll
            // rather than letting a quest land on an unwearable objective.
            if (group.Items.Count == 0)
            {
                continue;
            }
            GearCategoriesWeighting[groupName] = group.Weight;
        }

        ItemTierList = tierList.loadTierList(System.IO.Path.Combine(modPath, "assets/data/itemtierlist.jsonc"));

        // Deliberately EMPTY here - filled by CompileAllCategories at the final load stage. See
        // that method for why compiling this early would be wrong.
        ResolvedCategories = new Dictionary<string, ResolvedWeaponCategory>();
        WeaponCategoriesWeighting = new Dictionary<string, double>();

        var handoverCategoriesFile = modHelper.GetJsonDataFromFile<HandoverCategoriesFile>(modPath, "assets/data/handovercategories.jsonc");
        HandoverCategories = BuildHandoverCategories(handoverCategoriesFile, databaseService);
        HandoverItemBlacklist = new HashSet<MongoId>(handoverCategoriesFile.ItemBlacklist);

        QuestWhitelist = LoadQuestWhitelist(modPath, modHelper);

        (WeaponEquivalents, CategoryAdditions, ModCategoryAdditions, CategoryWeights) = LoadWeaponCompatibilityFiles(modPath, modHelper);
    }

    // Merges every compat file's "modCategoryAdditions" into the raw mod categories (e.g. dropping
    // a mod's suppressors into "All Silencers"). MUST run before CompileAllCategories, because
    // compilation bakes mod-category membership into each weapon category's required/forbidden
    // groups - anything merged afterwards would never reach the client.
    //
    // Unknown mod-category names are reported rather than created: unlike weapon categories, a mod
    // category only means something if a weapon category references it by name, so a typo here is
    // a mistake, not a definition.
    public static void ApplyModCategoryAdditions()
    {
        foreach (var (modCategoryName, modIds) in ModCategoryAdditions)
        {
            if (!WeaponCategories.ModCategories.TryGetValue(modCategoryName, out var modCategory))
            {
                Leaves.Utils.Instance.printColor(
                    $"[Questrandomizer] Compat files add to unknown mod category '{modCategoryName}' - skipped. "
                    + "(Mod categories must exist in weaponcategories.jsonc; only weapon categories can be created by compat files.)",
                    LogTextColor.Red);
                continue;
            }

            var existing = new HashSet<MongoId>(modCategory.Mods);
            int added = 0;
            foreach (var modId in modIds)
            {
                if (existing.Add(modId))
                {
                    modCategory.Mods.Add(modId);
                    added++;
                }
            }
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Mod category '{modCategoryName}' +{added} mod(s) from compat files.",
                LogTextColor.Cyan);
        }
    }

    // Compiles every raw weapon category into its ResolvedWeaponCategory. Runs at the FINAL load
    // stage (not the constructor) for two reasons that both come down to "wait until every mod has
    // loaded":
    //   1. Compilation bakes mod-category ids into the required/forbidden groups, so it has to
    //      happen AFTER ApplyModCategoryAdditions has merged modded suppressors/bipods/etc in.
    //   2. Nothing reads ResolvedCategories before this point anyway - quest generation is
    //      per-request, long after startup - so there's no reason to do it early.
    public static void CompileAllCategories()
    {
        ResolvedCategories.Clear();
        foreach (var (categoryName, rawCategory) in WeaponCategories.Categories)
        {
            MigrateLegacyModFormat(categoryName, rawCategory);
            ResolvedCategories[categoryName] = CompileCategory(categoryName, rawCategory);
        }

        // Provisional weighting - FinalizeCategories rebuilds it once empty categories are known.
        WeaponCategoriesWeighting.Clear();
        foreach (var (categoryName, resolved) in ResolvedCategories)
        {
            WeaponCategoriesWeighting[categoryName] = resolved.Weight;
        }
    }

    // Loads EVERY *.jsonc in config/weaponCompatibility/ and merges their contents, so
    // compatibility for different mods can live in separate drop-in files (add or remove a whole
    // file rather than editing one big shared list). Same folder-scan pattern as
    // LoadQuestWhitelist. A missing folder just means no compatibility (fine on a fresh install).
    // Two files naming the same category simply contribute to the same list.
    private static (List<WeaponEquivalentEntry> Equivalents, Dictionary<string, List<MongoId>> Additions, Dictionary<string, List<MongoId>> ModAdditions, Dictionary<string, double> Weights)
        LoadWeaponCompatibilityFiles(string modPath, ModHelper modHelper)
    {
        var equivalents = new List<WeaponEquivalentEntry>();
        var additions = new Dictionary<string, List<MongoId>>();
        var modAdditions = new Dictionary<string, List<MongoId>>();
        var weights = new Dictionary<string, double>();
        string folder = System.IO.Path.Combine(modPath, "config/weaponCompatibility");

        if (!System.IO.Directory.Exists(folder))
        {
            return (equivalents, additions, modAdditions, weights);
        }

        static void Merge(Dictionary<string, List<MongoId>> target, Dictionary<string, List<MongoId>> source)
        {
            foreach (var (name, ids) in source)
            {
                if (!target.TryGetValue(name, out var list))
                {
                    list = new List<MongoId>();
                    target[name] = list;
                }
                list.AddRange(ids);
            }
        }

        // Two files can both want the same invented category prominent - keep the larger weight so a
        // mod that asked for visibility isn't quietly outvoted by one that left it at the default.
        static void MergeWeights(Dictionary<string, double> target, Dictionary<string, double> source)
        {
            foreach (var (name, weight) in source)
            {
                if (!target.TryGetValue(name, out var existing) || weight > existing)
                {
                    target[name] = weight;
                }
            }
        }

        foreach (var filePath in System.IO.Directory.GetFiles(folder, "*.jsonc"))
        {
            string relativePath = System.IO.Path.Combine("config/weaponCompatibility", System.IO.Path.GetFileName(filePath));
            var file = modHelper.GetJsonDataFromFile<WeaponCompatibilityFile>(modPath, relativePath);
            equivalents.AddRange(file.Equivalents);
            Merge(additions, file.CategoryAdditions);
            Merge(modAdditions, file.ModCategoryAdditions);
            MergeWeights(weights, file.CategoryWeights);
        }

        return (equivalents, additions, modAdditions, weights);
    }

    // Converts a legacy-format category ({"mods-inclusive": {"name": bool}}) into the new plain
    // lists, in memory only. The old bool encoded "AND-merge into the previous entry" - but every
    // real config only ever built plain AND-chains for inclusive (which is what RequiredMods
    // means) and plain unions for exclusive (ForbiddenMods), so the keys ARE the migration.
    private static void MigrateLegacyModFormat(string categoryName, WeaponCategoryEntry category)
    {
        if (category.LegacyModsInclusive == null && category.LegacyModsExclusive == null)
        {
            return;
        }

        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer] Weapon category '{categoryName}' uses the old mods-inclusive/mods-exclusive format - "
            + "converted automatically this run. Please update it to \"requiredMods\"/\"forbiddenMods\" lists (see the shipped weaponcategories.jsonc).",
            LogTextColor.Yellow);

        if (category.LegacyModsInclusive != null)
        {
            category.RequiredMods = category.LegacyModsInclusive.Keys.ToList();
        }
        if (category.LegacyModsExclusive != null)
        {
            category.ForbiddenMods = category.LegacyModsExclusive.Keys.ToList();
        }
    }

    // Compiles one category into every shape its consumers need. The interesting part is the
    // cartesian product: "requiredMods": ["Big Silencers", "Bipods"] has to reach the client as
    // OR-of-AND groups (the client counts a kill if the weapon matches ANY one group in
    // weaponModsInclusive, and a group matches only if ALL its mods are fitted). So "one mod
    // from each required category" becomes every possible (silencer, bipod) pairing as its own
    // group. Starting from one empty group and multiplying category-by-category produces exactly
    // that - and requiredMods: [] naturally compiles to no groups at all, since the seed empty
    // group only survives into the output if something got multiplied into it.
    private static ResolvedWeaponCategory CompileCategory(string categoryName, WeaponCategoryEntry category)
    {
        var requiredGroups = new List<List<string>> { new() };
        foreach (var modCategoryName in category.RequiredMods)
        {
            if (!WeaponCategories.ModCategories.TryGetValue(modCategoryName, out var modCategory))
            {
                Leaves.Utils.Instance.printColor($"[Questrandomizer] Weapon category '{categoryName}' requires unknown mod category '{modCategoryName}' - skipped.", LogTextColor.Red);
                continue;
            }

            var multiplied = new List<List<string>>();
            foreach (var existingGroup in requiredGroups)
            {
                foreach (var mod in modCategory.Mods)
                {
                    multiplied.Add(new List<string>(existingGroup) { mod.ToString() });
                }
            }
            requiredGroups = multiplied;
        }
        if (requiredGroups.Count == 1 && requiredGroups[0].Count == 0)
        {
            requiredGroups.Clear();
        }
        if (requiredGroups.Count > 1000)
        {
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Weapon category '{categoryName}' compiles to {requiredGroups.Count} required-mod combinations - "
                + "that's a lot of data per quest. Consider requiring fewer/smaller mod categories.",
                LogTextColor.Yellow);
        }

        var forbiddenMods = new List<List<string>>();
        var seenForbidden = new HashSet<string>();
        foreach (var modCategoryName in category.ForbiddenMods)
        {
            if (!WeaponCategories.ModCategories.TryGetValue(modCategoryName, out var modCategory))
            {
                Leaves.Utils.Instance.printColor($"[Questrandomizer] Weapon category '{categoryName}' forbids unknown mod category '{modCategoryName}' - skipped.", LogTextColor.Red);
                continue;
            }
            foreach (var mod in modCategory.Mods)
            {
                if (seenForbidden.Add(mod.ToString()))
                {
                    forbiddenMods.Add(new List<string> { mod.ToString() });
                }
            }
        }

        return new ResolvedWeaponCategory
        {
            Name = categoryName,
            Weight = category.Weight,
            Weapons = new List<MongoId>(category.Weapons),
            AnyWeapon = category.Weapons.Count == 0 && !category.HasSelectors,
            Calibers = category.Calibers,
            ExcludeWeapons = category.ExcludeWeapons,
            FireRate = category.FireRate,
            HasHandListedWeapons = category.Weapons.Count > 0,
            RequiredMods = category.RequiredMods,
            ForbiddenMods = category.ForbiddenMods,
            CompiledRequiredGroups = requiredGroups,
            CompiledForbiddenMods = forbiddenMods,
        };
    }

    // The item-class id every real weapon descends from, and the class for map-mounted
    // stationary guns (the Utyos NSV etc.) that would otherwise match caliber selectors.
    private static readonly MongoId WeaponBaseClass = "5422acb9af1c889c16000029";
    private static readonly MongoId StationaryWeaponClass = "5cdeb229d7f00c000e7ce174";

    // Expands every category's selector fields (calibers + optional fire-rate) into real weapon ids by
    // scanning the live item database. Called ONCE, at the PostSptModLoader stage (see
    // QuestrandomizerLateSetup) - deliberately later than this class's constructor, so weapons added
    // by OTHER mods during their own OnLoad are already in the database and join their matching
    // categories automatically.
    //
    // A selector is a straight query over the item DB: DB -> selector -> group. Every weapon is
    // judged on its OWN row - its own caliber, its own fire rate. Equivalence is deliberately NOT
    // consulted here: a ShinyAirdropGuns golden reskin, or a WTT AN-94, is a real, distinct item, so
    // if it matches it joins the group by its own name. (Equivalence is a hand-list concept -
    // "killing with the shiny counts on a quest that named the base" - and it still applies to
    // hand-listed weapons via the ride-along; it just no longer suppresses what a DB query legitimately
    // matches. A base and its shiny both matching a caliber both appear, which is correct: they're two
    // different guns.)
    public static void ResolveDynamicWeapons(Dictionary<MongoId, TemplateItem> itemDB)
    {
        foreach (var resolved in ResolvedCategories.Values)
        {
            if (!resolved.HasSelectors)
            {
                continue;
            }

            // Calibers are the only candidate-BUILDER. A category with only a fire-rate filter (no
            // calibers) has no builder, so every weapon is a candidate and the fire rate alone decides.
            bool hasCandidateSelector = resolved.Calibers.Count > 0;

            var normalizedCalibers = new List<string>();
            foreach (var caliber in resolved.Calibers)
            {
                normalizedCalibers.Add(NormalizeCaliber(caliber));
            }
            var existing = new HashSet<MongoId>(resolved.Weapons);
            int matched = 0, added = 0;

            foreach (var (itemId, item) in itemDB)
            {
                if (item.Type != "Item" || item.Properties?.AmmoCaliber == null)
                {
                    continue; // not a shootable weapon
                }
                if (!IsUnderClass(itemDB, itemId, WeaponBaseClass) || IsUnderClass(itemDB, itemId, StationaryWeaponClass))
                {
                    continue;
                }
                if (item.Properties.QuestItem == true)
                {
                    continue;
                }

                // Calibers form the candidate set (or "all weapons" when the category declared none).
                bool candidate = !hasCandidateSelector;
                if (hasCandidateSelector)
                {
                    foreach (var caliber in normalizedCalibers)
                    {
                        if (NormalizeCaliber(item.Properties.AmmoCaliber).StartsWith(caliber))
                        {
                            candidate = true;
                            break;
                        }
                    }
                }
                if (!candidate)
                {
                    continue;
                }

                // The fire-rate filter then NARROWS the candidate set.
                if (!PassesFireRate(item.Properties.BFirerate, resolved.FireRate))
                {
                    continue;
                }

                matched++;
                if (resolved.ExcludeWeapons.Contains(itemId) || !existing.Add(itemId))
                {
                    continue; // hand-excluded, or already hand-listed
                }
                resolved.Weapons.Add(itemId);
                added++;
            }

            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Category '{resolved.Name}' selectors matched {matched} weapons, added {added} new (rest were hand-listed/excluded).",
                LogTextColor.Cyan);
        }
    }

    // Merges every compat file's "weaponCategoryAdditions" into the resolved categories. Runs at the
    // final load stage (all mods loaded), and deliberately does NOT validate the ids here -
    // FinalizeCategories runs straight after and prunes anything whose mod isn't installed, then
    // drops a category left empty. So a compat file can freely reference a mod you don't have.
    //
    // An unknown category name is CREATED as a weapon list rather than warned about: that's what
    // makes a mod-only category (e.g. "WTT-Armory") definable entirely from the mod's own compat
    // file. Its roll weight comes from weaponCategoryWeights (default 1). Categories needing
    // required/forbidden mods should still be declared in weaponcategories.jsonc - additions then
    // just top them up, and a weight aimed at such a pre-existing category is ignored (that file
    // stays the source of truth for the built-in categories' weights).
    public static void ApplyCategoryAdditions()
    {
        foreach (var (categoryName, weaponIds) in CategoryAdditions)
        {
            if (!ResolvedCategories.TryGetValue(categoryName, out var resolved))
            {
                // A category the compat files invented. Take its weight from weaponCategoryWeights if
                // given, else the old default of 1 - so a mod that wants its category to actually get
                // rolled can say so, instead of being stuck at a weight that never surfaces.
                double newWeight = 1;
                if (CategoryWeights.TryGetValue(categoryName, out var requestedWeight))
                {
                    newWeight = requestedWeight;
                }

                resolved = new ResolvedWeaponCategory
                {
                    Name = categoryName,
                    Weight = newWeight,
                    Weapons = new List<MongoId>(),
                    AnyWeapon = false, // defined by its explicit weapons, so it must be pruned/dropped if absent
                    Calibers = new List<string>(),
                    ExcludeWeapons = new List<MongoId>(),
                    FireRate = null,
                    // A compat-file category is an explicit weapon list (no selectors), so it counts as
                    // hand-listed and keeps equivalence - SkipsEquivalence is false either way here.
                    HasHandListedWeapons = true,
                    RequiredMods = new List<string>(),
                    ForbiddenMods = new List<string>(),
                    CompiledRequiredGroups = new List<List<string>>(),
                    CompiledForbiddenMods = new List<List<string>>(),
                };
                ResolvedCategories[categoryName] = resolved;
                // Debug-gated: this fires before FinalizeCategories prunes ids for absent mods, so a
                // category logged here can still be dropped moments later (a compat file bundled for a
                // mod you don't run). FinalizeCategories' summary is the authoritative, always-on line.
                Leaves.Utils.Instance.printColor(
                    $"[Questrandomizer] Compat files created new weapon category '{categoryName}' (weight {newWeight}).",
                    LogTextColor.Cyan, SptLogLevel.Info, true);
            }
            else if (CategoryWeights.ContainsKey(categoryName))
            {
                // A weight was given for a category that already exists in weaponcategories.jsonc.
                // We don't let a compat file rebalance a built-in category, so keep the curated weight
                // and say so, rather than silently doing nothing with the requested value.
                Leaves.Utils.Instance.printColor(
                    $"[Questrandomizer] Compat files set a weight for '{categoryName}', but it already exists "
                    + $"in weaponcategories.jsonc - keeping that file's weight ({resolved.Weight}).",
                    LogTextColor.Yellow);
            }

            var existing = new HashSet<MongoId>(resolved.Weapons);
            int added = 0;
            foreach (var weaponId in weaponIds)
            {
                if (existing.Add(weaponId))
                {
                    resolved.Weapons.Add(weaponId);
                    added++;
                }
            }

            // Debug-gated for the same reason as the creation line above: pre-prune bookkeeping.
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Category '{categoryName}' +{added} weapon(s) from compat files.",
                LogTextColor.Cyan, SptLogLevel.Info, true);
        }
    }

    // Runs at the final load stage, right after ResolveDynamicWeapons - so EVERY mod's items are
    // already in the database. Two safety passes that let a mod-specific category (e.g. one listing
    // WTT weapons) simply vanish for players who don't run that mod, instead of producing a quest
    // that asks for a weapon nobody owns:
    //   1. Drop any hand-listed weapon id that STILL isn't in the database. By this stage a missing
    //      id genuinely means "that weapon's mod isn't installed" (not a load-order timing gap), so
    //      it's safe to prune - and it also quietly forgives typo'd ids.
    //   2. REMOVE any category that emptied out entirely (had weapon sources that all pruned away)
    //      from ResolvedCategories, not just from the roll weighting. ResolvedCategories is the shared
    //      list the handbook and re-roll web UI enumerate too (see QuestHandbookGeneration), so a
    //      compat file bundled for a mod you don't run must vanish from those, not linger as an empty
    //      category whose sheet misleadingly reads "any weapon counts". A deliberate "any weapon"
    //      category (empty BY DESIGN - see ResolvedWeaponCategory.AnyWeapon) is kept.
    //   3. Rebuild the roll weighting from the survivors. (The constructor's weighting was
    //      provisional - selectors hadn't run yet.)
    public static void FinalizeCategories(Dictionary<MongoId, TemplateItem> itemDB)
    {
        int droppedIds = 0;
        foreach (var resolved in ResolvedCategories.Values)
        {
            droppedIds += resolved.Weapons.RemoveAll(id => !itemDB.ContainsKey(id));
        }

        var emptied = new List<string>();
        foreach (var (name, resolved) in ResolvedCategories)
        {
            // An emptied category (had weapons/selectors that all pruned away) means its mod isn't
            // installed - drop it. AnyWeapon categories are empty on purpose, so they stay.
            if (resolved.Weapons.Count == 0 && !resolved.AnyWeapon)
            {
                emptied.Add(name);
            }
        }
        foreach (var name in emptied)
        {
            ResolvedCategories.Remove(name);
        }

        WeaponCategoriesWeighting.Clear();
        foreach (var (name, resolved) in ResolvedCategories)
        {
            WeaponCategoriesWeighting[name] = resolved.Weight;
        }

        string emptiedNote = emptied.Count > 0 ? $" Removed empty (missing mod?): {string.Join(", ", emptied)}." : "";
        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer] Finalized weapon categories: {WeaponCategoriesWeighting.Count} rollable, "
            + $"dropped {droppedIds} weapon id(s) whose mod isn't installed, {emptied.Count} removed as empty.{emptiedNote}",
            LogTextColor.Cyan);
    }

    // "7.62x25" / "Caliber762x25TT" / "9x19" / "Caliber9x19PARA" all reduce to a comparable
    // core: lowercase, dots stripped, "caliber" prefix stripped. Matching is prefix-based so the
    // human form doesn't need BSG's trailing tags (TT/PARA/NATO...).
    private static string NormalizeCaliber(string caliber)
    {
        string normalized = caliber.ToLowerInvariant().Replace(".", "");
        return normalized.StartsWith("caliber") ? normalized["caliber".Length..] : normalized;
    }

    private static bool IsUnderClass(Dictionary<MongoId, TemplateItem> itemDB, MongoId itemId, MongoId classId)
    {
        MongoId current = itemDB[itemId].Parent;
        while (current != "")
        {
            if (current == classId)
            {
                return true;
            }
            if (!itemDB.TryGetValue(current, out var parentItem))
            {
                return false;
            }
            current = parentItem.Parent;
        }
        return false;
    }

    // A weapon passes the category's fire-rate filter when its full-auto RPM is within the bounds.
    // A null bound = no filter (always passes). A null fireRate = the weapon has no fire rate at all
    // (a bolt-action, most shotguns), which FAILS any bound that's set - so such guns never slip into
    // a "high rate of fire" category. Above/Below are strict and independent.
    private static bool PassesFireRate(double? fireRate, FireRateBound? bound)
    {
        if (bound == null)
        {
            return true;
        }
        if (fireRate == null)
        {
            return false;
        }
        if (bound.Above != null && fireRate.Value <= bound.Above.Value)
        {
            return false; // must be strictly greater than the lower bound
        }
        if (bound.Below != null && fireRate.Value >= bound.Below.Value)
        {
            return false; // must be strictly less than the upper bound
        }
        return true;
    }

    // "Whitelist" entries start as empty item lists, then get auto-filled by scanning every item
    // in the game for one whose _parent matches - so e.g. every item under the "Medical Supplies"
    // parent category ends up listed here automatically, without anyone hand-maintaining that
    // list. "Custom" categories are hand-picked lists that don't map to a real BSG parent category
    // (e.g. "Cigarettes" isn't a single parent category in the game's own data).
    private static Dictionary<string, List<MongoId>> BuildHandoverCategories(HandoverCategoriesFile file, DatabaseService databaseService)
    {
        var categories = new Dictionary<string, List<MongoId>>();

        foreach (var parentId in file.CategoryWhitelist)
        {
            categories[parentId.ToString()] = new List<MongoId>();
        }

        foreach (var (customCategoryName, items) in file.CustomCategories)
        {
            categories[customCategoryName] = items;
        }

        foreach (var item in databaseService.GetItems().Values)
        {
            if (categories.TryGetValue(item.Parent.ToString(), out var list))
            {
                list.Add(item.Id);
            }
        }

        return categories;
    }

    // Every *.jsonc file directly inside config/questwhitelists/enabled/ contributes its whole
    // "whitelist" list - concatenated together into one combined whitelist.
    private static List<MongoId> LoadQuestWhitelist(string modPath, ModHelper modHelper)
    {
        var whitelist = new List<MongoId>();
        string enabledFolder = System.IO.Path.Combine(modPath, "config/questwhitelists/enabled");

        if (!System.IO.Directory.Exists(enabledFolder))
        {
            return whitelist;
        }

        foreach (var filePath in System.IO.Directory.GetFiles(enabledFolder, "*.jsonc"))
        {
            string relativePath = System.IO.Path.Combine("config/questwhitelists/enabled", System.IO.Path.GetFileName(filePath));
            var file = modHelper.GetJsonDataFromFile<QuestWhitelistFile>(modPath, relativePath);
            whitelist.AddRange(file.Whitelist);
        }

        return whitelist;
    }
}
