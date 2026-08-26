using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SptLogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

namespace Leaves_Questrandomizer;

// C# equivalent of the handbook/category-browsing parts of the TS original's mod.ts
// (setupHandbookCategories, generateCategoryItem, generateWeaponCategorySheet,
// addCategoriesToWeapons/addCategoriesToWeapon) plus the locale-writing half of
// LeavesLocaleGeneration.ts (addLocaleToAll/addLocaleTo/addFullLocale/addWeaponGroupToWeapon/
// addEquivalentToWeapon). Purely cosmetic/browsing content - it doesn't affect how quests get
// randomized at all, it just makes the weapon/mod categories quest generation already uses
// visible to the player, as an in-game handbook section they can flip through.
//
// IMPORTANT ARCHITECTURAL DIFFERENCE FROM EVERYTHING ELSE IN THIS MOD: everything else writes
// locale text through a LocaleWorkspace (see LocaleWorkspace.cs), because quest text is
// per-profile - two different players can see two different randomized quest descriptions for
// what's internally the "same" quest slot. Handbook categories are NOT per-profile - there's
// only one "AK category" for the whole server, the same for every player, created once when the
// server starts. So instead of LocaleWorkspace, this writes directly into the REAL, shared
// locale data - the same AddTransformer trick TreasureBox's FakeVodkaFix uses (see that file for
// the full explanation of why a plain dictionary write would silently do nothing).
public class QuestHandbookGeneration
{
    // The base game item this mod clones to make each category's "book" item. It doesn't matter
    // what it originally was - CreateItemFromClone below immediately overrides its name, looks,
    // and text, so all that's borrowed from it is "a valid, sellable, examine-able item shape".
    private static readonly MongoId CategoryItemBaseTemplate = "574eb85c245977648157eec3";
    private static readonly string CategoryItemPrefabPath = "assets/content/items/barter/item_info_book_bakeezy/item_info_book_bakeezy.bundle";
    // The BSG item-category ("Info") every category item gets filed under in the item tree -
    // unrelated to the mod's OWN handbook categories being set up below, this is just "what kind
    // of item is this" bookkeeping the game needs for every item, custom or not.
    private static readonly MongoId InfoItemParent = "567849dd4bdc2d150f8b456e";

    private readonly string modPath;
    private readonly DatabaseService databaseService;
    private readonly CustomItemService customItemService;
#pragma warning disable CS0618 // ConfigServer is obsolete (see BarterEconomy.ApplyDebugFastResupply's comment) but still the only way to reach TraderConfig/ItemConfig in 4.0.13.
    private readonly ConfigServer configServer;
#pragma warning restore CS0618

    // Same shared id store the quest generator uses for its "QG_WeaponMastery0"-style ids (see
    // Questrandomizer.cs) - a category's item needs the SAME id every restart too, for the same
    // reason a generated quest does, so its id lives in this one shared assets/generated/ids.jsonc
    // file rather than a second, separate file.
    private readonly Leaves.IdStore generatedIds;

    private MongoId topLevelHandbookCategoryId;
    private MongoId weaponCategoryHandbookId;
    private MongoId modCategoryHandbookId;
    private MongoId gearCategoryHandbookId;

    // Gear group names and weapon/mod category names both key into the SAME shared generatedIds store,
    // so a gear group that happened to share a name with a weapon category would clash on its handbook
    // item id. Prefix every gear-group id-store key with this to keep the two namespaces apart.
    private const string GearIdKeyPrefix = "GearCategory_";

    public QuestHandbookGeneration(
        string modPath,
        DatabaseService databaseService,
        CustomItemService customItemService,
#pragma warning disable CS0618
        ConfigServer configServer,
#pragma warning restore CS0618
        Leaves.IdStore generatedIds,
        Dictionary<MongoId, List<MongoId>> transitiveEquivalents)
    {
        this.modPath = modPath;
        this.databaseService = databaseService;
        this.customItemService = customItemService;
        this.configServer = configServer;
        this.generatedIds = generatedIds;
        this.transitiveEquivalents = transitiveEquivalents;
    }

    // weapon -> every weapon that TRANSITIVELY counts as it, from BOTH sources (config-file equivalents
    // AND runtime ones like ShinyAirdropGuns' clones), flattened once in Questrandomizer.LateOnLoad
    // before this class is built - see QuestrandomizerCompatibility.BuildTransitiveEquivalents. Used for
    // the category-sheet "= <name>" lines and the "Part of category" stamp on equivalents - but ONLY for
    // hand-listed/mixed categories (ResolvedWeaponCategory.SkipsEquivalence == false); a purely
    // selector-based category never involves equivalence.
    private readonly Dictionary<MongoId, List<MongoId>> transitiveEquivalents;

    // Everything below runs exactly ONCE, at server startup (called from Questrandomizer.OnLoad),
    // not per-profile - there's no "player" concept anywhere in this file.
    public void Generate()
    {
        // Config-file weapon equivalents (config/weaponCompatibility.jsonc) get their description
        // text ("Equivalent to: [x]") written first, matching the TS original's own order (it did
        // this as part of constructing its quest manager, before setting up handbook categories).
        foreach (var entry in Settings.WeaponEquivalents)
        {
            RegisterEquivalent(entry.OriginalWeaponId, entry.EquivalentWeaponId);
        }

        // Copy category membership onto TRANSITIVE equivalents, so a shiny clone of a modded gun - a
        // second-order equivalent (shiny -> moddedSVD -> vanillaSVD) - inherits the categories of
        // whichever hop is the real member. Driving off the flattened graph handles any depth.
        // AddCategoriesToWeapon itself skips selector-based categories (SkipsEquivalence), so only
        // hand-listed/mixed categories propagate; registeredGroupLines dedupes the overlap.
        foreach (var (original, descendants) in transitiveEquivalents)
        {
            foreach (var descendant in descendants)
            {
                AddCategoriesToWeapon(original, descendant);
            }
        }

        SetupHandbookCategories();
        GenerateWeaponCategorySheet();
        GenerateGearCategorySheet();
    }

    // One equivalent pair's full description-text treatment: an "Equivalent to: [original]" line on the
    // equivalent weapon, plus every "Part of category: [X]" line the original carries for a HAND-LISTED
    // (or mixed) category. Called for config-file equivalents above, AND (via QuestrandomizerCompatibility's
    // handler - see Questrandomizer.OnLoad) for every pair other mods register at runtime. Safe to call at
    // any point during startup: the writes are AddTransformer-based, so they apply on every locale read no
    // matter when they were registered.
    public void RegisterEquivalent(MongoId originalId, MongoId equivalentId)
    {
        AddEquivalentToWeapon(originalId, equivalentId);
        AddCategoriesToWeapon(originalId, equivalentId);
    }

    // Creates the 3 handbook categories every category item below gets filed under: one top-level
    // "Questrandomizer" folder, and "Weapon categories"/"Mod categories" sub-folders inside it -
    // exactly like the "Ammo"/"Weapons"/etc top-level folders the base game already has in its own
    // handbook screen.
    private void SetupHandbookCategories()
    {
        topLevelHandbookCategoryId = generatedIds.GetOrCreate("TopLevelHandbookCategory");
        weaponCategoryHandbookId = generatedIds.GetOrCreate("WeaponCategoryHandbookID");
        modCategoryHandbookId = generatedIds.GetOrCreate("ModCategoryHandbookID");
        gearCategoryHandbookId = generatedIds.GetOrCreate("GearCategoryHandbookID");

        var questrandomizerCategory = new HandbookCategory
        {
            Id = topLevelHandbookCategoryId,
            ParentId = null,
            Icon = "/files/handbook/icon_barter_valuables.png",
            Color = "",
            Order = "14",
        };
        var weaponCategory = new HandbookCategory
        {
            Id = weaponCategoryHandbookId,
            ParentId = topLevelHandbookCategoryId,
            Icon = "/files/handbook/icon_weapons_pistols.png",
            Color = "",
            Order = "2",
        };
        var modCategory = new HandbookCategory
        {
            Id = modCategoryHandbookId,
            ParentId = topLevelHandbookCategoryId,
            Icon = "/files/handbook/icon_barter_tools.png",
            Color = "",
            Order = "1",
        };
        var gearCategory = new HandbookCategory
        {
            Id = gearCategoryHandbookId,
            ParentId = topLevelHandbookCategoryId,
            Icon = "/files/handbook/icon_gear_armor.png",
            Color = "",
            Order = "3",
        };

        // The top-level folder's own display name - written to every language at once with a
        // rainbow effect, same as the TS original, since it's just a fun mod-branding label rather
        // than something that needs real translation.
        WriteLocaleToAll(topLevelHandbookCategoryId.ToString(), Leaves.Utils.Instance.RTT_Rainbowify("[Questrandomizer]"));

        // databaseService.GetHandbook() returns the real, shared handbook table directly (unlike
        // locale text, this ISN'T behind a LazyLoad re-deserialize-on-every-read wrapper), so a
        // plain List.Add here is enough - it doesn't need the AddTransformer trick.
        databaseService.GetHandbook().Categories.Add(questrandomizerCategory);
        databaseService.GetHandbook().Categories.Add(weaponCategory);
        databaseService.GetHandbook().Categories.Add(modCategory);
        databaseService.GetHandbook().Categories.Add(gearCategory);
    }

    // Clones a plain "book" item into a new one representing a single category (e.g. "AKs", or
    // the "Suppressors" mod-group) - its only purpose is to exist in the handbook screen so a
    // player can click it and read its description (set later, in GenerateWeaponCategorySheet).
    // It's not a real, obtainable item - see the fence/item-config blacklisting right after this
    // gets called.
    private MongoId GenerateCategoryItem(string categoryKey, MongoId handbookParentId)
    {
        MongoId newId = generatedIds.GetOrCreate(categoryKey);

        var newItemDetails = new NewItemFromCloneDetails
        {
            ItemTplToClone = CategoryItemBaseTemplate,
            OverrideProperties = new TemplateItemProperties
            {
                Prefab = new Prefab
                {
                    Path = CategoryItemPrefabPath,
                    Rcid = "",
                },
            },
            NewId = newId.ToString(),
            ParentId = InfoItemParent.ToString(),
            HandbookParentId = handbookParentId.ToString(),
            FleaPriceRoubles = 10000,
            HandbookPriceRoubles = 10000,
            // Placeholder-only - GenerateWeaponCategorySheet immediately overwrites the real name/
            // description for every language right after creating this item. If this placeholder
            // text is ever actually visible in-game, something above it broke.
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new LocaleDetails
                {
                    Name = "ERROR",
                    ShortName = "ERROR",
                    Description = "IF YOU EVER SEE THIS SOMETHING HAS GONE WRONG!",
                },
            },
        };

        customItemService.CreateItemFromClone(newItemDetails);
        return newId;
    }

    // The big one: creates one category item per weapon category AND per mod category, then
    // builds each one's in-game description as a readable "sheet" listing every weapon/mod that
    // belongs to it (plus, for weapon categories, what mods are required/forbidden on them) - and
    // writes the whole thing to a categories/categories_<language>.txt file too, for players who'd
    // rather read it outside the game.
    private void GenerateWeaponCategorySheet()
    {
        // ConfigServer.GetConfig<T>() is marked obsolete by SPT itself but is still the only way
        // to reach TraderConfig/ItemConfig in 4.0.13 - same situation as TreasureBox/ShinyAirdropGuns.
#pragma warning disable CS0618
        var traderConfig = configServer.GetConfig<TraderConfig>();
        var itemConfig = configServer.GetConfig<ItemConfig>();
        var scavCaseConfig = configServer.GetConfig<ScavCaseConfig>();
#pragma warning restore CS0618

        // Category items aren't meant to be sold, bought, or found - blacklisting them from the
        // Fence trader, from item-config's general blacklist (loot pools, etc), and from the scav
        // case reward pool keeps them strictly a "look at this in your handbook" thing.
        foreach (var categoryName in Settings.ResolvedCategories.Keys)
        {
            MongoId newId = GenerateCategoryItem(categoryName, weaponCategoryHandbookId);
            traderConfig.Fence.Blacklist.Add(newId);
            itemConfig.Blacklist.Add(newId);
            scavCaseConfig.RewardItemBlacklist.Add(newId);
        }
        foreach (var modGroupName in Settings.WeaponCategories.ModCategories.Keys)
        {
            MongoId newId = GenerateCategoryItem(modGroupName, modCategoryHandbookId);
            traderConfig.Fence.Blacklist.Add(newId);
            itemConfig.Blacklist.Add(newId);
            scavCaseConfig.RewardItemBlacklist.Add(newId);
        }

        // Every language the game itself ships gets its own sheet built independently - phrases
        // come from QuestLocaleGeneration.GetLoc (falls back to English for anything untranslated,
        // same as every other piece of text this mod generates).
        foreach (var language in databaseService.GetLocales().Global.Keys)
        {
            var sheet = new System.Text.StringBuilder();

            WriteLocale(
                language,
                weaponCategoryHandbookId.ToString(),
                Leaves.Utils.Instance.RTT_Color(QuestLocaleGeneration.GetLoc("WeaponCategory"), Leaves.RTT_Colors.GREEN));

            foreach (var (categoryName, weaponGroup) in Settings.ResolvedCategories)
            {
                string sheetCategoryLabel = QuestLocaleGeneration.GetLoc("SheetCategory");
                string weaponCategoryLabel = categoryName;

                var categorySheet = new System.Text.StringBuilder();
                categorySheet.Append(Leaves.Utils.Instance.RTT_Size($"[{sheetCategoryLabel}: {weaponCategoryLabel}]", "+4px"));
                categorySheet.Append("\n-----------------------------\n");

                if (weaponGroup.Weapons.Count == 0)
                {
                    categorySheet.Append($"\t{QuestLocaleGeneration.GetLoc("AnyWeapon")}\n");
                }
                else
                {
                    // Each member, and (for hand-listed/mixed categories) its equivalents indented under
                    // it - answering "what can I bring for this?", since an equivalent counts for the quest
                    // anywhere its original does. A PURELY selector-based category (SkipsEquivalence) shows
                    // members ONLY: equivalence doesn't apply to it, and a shiny/modded gun that belongs is
                    // already a member in its own right (the selector matched it). An equivalent that IS a
                    // listed member is skipped as an indented line so nothing prints twice.
                    var members = new HashSet<MongoId>(weaponGroup.Weapons);
                    foreach (var weapon in weaponGroup.Weapons)
                    {
                        categorySheet.Append($"\t{Leaves.Utils.Instance.getLocale(language, weapon.ToString(), " Name")}\n");
                        if (weaponGroup.SkipsEquivalence || !transitiveEquivalents.TryGetValue(weapon, out var equivalents))
                        {
                            continue;
                        }
                        foreach (var equivalent in equivalents)
                        {
                            if (members.Contains(equivalent))
                            {
                                continue; // already printed above as a member in its own right
                            }
                            categorySheet.Append($"\t\t= {Leaves.Utils.Instance.getLocale(language, equivalent.ToString(), " Name")}\n");
                        }
                    }
                }
                categorySheet.Append('\n');

                // Required mods are all-of (joined AND), forbidden mods are none-of (joined OR) -
                // fixed semantics since the schema overhaul, no more per-entry merge flags.
                categorySheet.Append(ModListSheet(weaponGroup.RequiredMods, "RequiredMods", "AND"));
                categorySheet.Append(ModListSheet(weaponGroup.ForbiddenMods, "ForbiddenMods", "OR"));

                MongoId categoryId = generatedIds.GetOrCreate(categoryName);
                string categorySheetText = categorySheet.ToString();
                WriteFullLocale(language, categoryId.ToString(), weaponCategoryLabel, weaponCategoryLabel, categorySheetText);

                sheet.Append(categorySheetText);
            }

            sheet.Append("\n-----------------------------\n");
            sheet.Append("-----------------------------\n");

            WriteLocale(
                language,
                modCategoryHandbookId.ToString(),
                Leaves.Utils.Instance.RTT_Color(QuestLocaleGeneration.GetLoc("ModCategory"), Leaves.RTT_Colors.BLUE));

            foreach (var (categoryName, modGroup) in Settings.WeaponCategories.ModCategories)
            {
                // Mod categories always mean "any one of these mods satisfies it" since the
                // schema overhaul (the never-used "together" flag is gone).
                string requirement = QuestLocaleGeneration.GetLoc("OneRequired");

                var modGroupSheet = new System.Text.StringBuilder();
                modGroupSheet.Append(Leaves.Utils.Instance.RTT_Size($"\n[{QuestLocaleGeneration.GetLoc("SheetCategory")}: {categoryName}] - {requirement}", "+4px"));
                modGroupSheet.Append("\n-----------------------------\n");

                foreach (var mod in modGroup.Mods)
                {
                    modGroupSheet.Append($"\t{Leaves.Utils.Instance.getLocale(language, mod.ToString(), " Name")}\n");
                }

                MongoId categoryId = generatedIds.GetOrCreate(categoryName);
                string modGroupSheetText = modGroupSheet.ToString();
                WriteFullLocale(language, categoryId.ToString(), categoryName, categoryName, modGroupSheetText);

                sheet.Append(modGroupSheetText);
            }

            string sheetPath = System.IO.Path.Combine(modPath, "categories", $"categories_{language}.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sheetPath)!);
            System.IO.File.WriteAllText(sheetPath, sheet.ToString());
        }

        AddCategoriesToWeapons();
        AddCategoriesToMods();
    }

    // Builds one "[Required Mods]"/"[Forbidden Mods]" block for a weapon category's sheet, the
    // listed names joined by a fixed connective ("AND" for required, "OR" for forbidden).
    // Returns "" (skips the block entirely) if the list is empty - matches the TS original only
    // emitting a title when there's actually something to put under it.
    private string ModListSheet(List<string> modCategoryNames, string titlePhraseKey, string connectivePhraseKey)
    {
        if (modCategoryNames.Count == 0)
        {
            return "";
        }

        var categorySheet = new System.Text.StringBuilder();
        categorySheet.Append(Leaves.Utils.Instance.RTT_Size($"[{QuestLocaleGeneration.GetLoc(titlePhraseKey)}]", "+4px"));
        categorySheet.Append("\n-----------------------------\n");

        bool firstDone = false;
        foreach (var modCategoryName in modCategoryNames)
        {
            if (firstDone)
            {
                categorySheet.Append($"\t{QuestLocaleGeneration.GetLoc(connectivePhraseKey)}\n");
            }
            categorySheet.Append($"\t{modCategoryName}\n");
            firstDone = true;
        }

        return categorySheet.ToString();
    }

    // Appends a "{QR} Part of category: [X]" line to every weapon in every configured weapon
    // category's own in-game description, so a player looking at a real weapon (not the category
    // item) can immediately see which quest-randomizer category it counts as.
    private void AddCategoriesToWeapons()
    {
        foreach (var (categoryName, weaponGroup) in Settings.ResolvedCategories)
        {
            foreach (var weaponId in weaponGroup.Weapons)
            {
                AddGroupLineToItem(categoryName, weaponId);
                Leaves.Utils.Instance.printColor($"[Questrandomizer] Adding {categoryName} to {weaponId}", LogTextColor.Yellow, SptLogLevel.Info, true);
            }
        }
    }

    // The gear-group counterpart of GenerateWeaponCategorySheet: one book item per gear group, filed
    // under the "Gear Categories" handbook folder, whose description lists every interchangeable piece
    // the group holds ("wear any one of these"). Same shape and same shared-locale writes as the weapon
    // sheet, minus the equivalents/required-mod machinery weapons carry (gear has none). Runs once at
    // startup; ends by stamping each real gear item with its "Part of category" line.
    private void GenerateGearCategorySheet()
    {
        // Gear-group book items, like the weapon/mod ones, exist only to be read in the handbook - keep
        // them out of Fence, the general loot/item blacklist, and the scav case reward pool so they
        // can't be bought or found.
#pragma warning disable CS0618 // ConfigServer obsolete but still the only route to TraderConfig/ItemConfig in 4.0.13 (see GenerateWeaponCategorySheet).
        var traderConfig = configServer.GetConfig<TraderConfig>();
        var itemConfig = configServer.GetConfig<ItemConfig>();
        var scavCaseConfig = configServer.GetConfig<ScavCaseConfig>();
#pragma warning restore CS0618

        foreach (var gearGroupName in Settings.GearCategories.Keys)
        {
            MongoId newId = GenerateCategoryItem(GearIdKeyPrefix + gearGroupName, gearCategoryHandbookId);
            traderConfig.Fence.Blacklist.Add(newId);
            itemConfig.Blacklist.Add(newId);
            scavCaseConfig.RewardItemBlacklist.Add(newId);
        }

        // One sheet per game language, same as the weapon sheet. The group's display label comes from
        // the same GetGearDisplayName the objective text uses, so "wearing: [FORT body armor]" and the
        // handbook entry always read identically.
        foreach (var language in databaseService.GetLocales().Global.Keys)
        {
            WriteLocale(
                language,
                gearCategoryHandbookId.ToString(),
                Leaves.Utils.Instance.RTT_Color(QuestLocaleGeneration.GetLoc("GearCategory"), Leaves.RTT_Colors.ORANGE));

            foreach (var (gearGroupName, gearGroup) in Settings.GearCategories)
            {
                string gearGroupLabel = QuestLocaleGeneration.GetGearDisplayName(gearGroupName);

                var groupSheet = new System.Text.StringBuilder();
                groupSheet.Append(Leaves.Utils.Instance.RTT_Size($"[{QuestLocaleGeneration.GetLoc("SheetCategory")}: {gearGroupLabel}]", "+4px"));
                groupSheet.Append("\n-----------------------------\n");
                groupSheet.Append($"\t{QuestLocaleGeneration.GetLoc("WearAnyOne")}\n");

                foreach (var itemId in gearGroup.Items)
                {
                    groupSheet.Append($"\t{Leaves.Utils.Instance.getLocale(language, itemId.ToString(), " Name")}\n");
                }

                MongoId gearGroupId = generatedIds.GetOrCreate(GearIdKeyPrefix + gearGroupName);
                WriteFullLocale(language, gearGroupId.ToString(), gearGroupLabel, gearGroupLabel, groupSheet.ToString());
            }
        }

        AddCategoriesToGear();
    }

    // Appends a "{QR} Part of category: [FORT body armor]" line to every gear piece in every group's
    // in-game description, exactly like AddCategoriesToWeapons does for weapons - so a player looking at
    // a real armor/helmet/rig can see which wear-objective group it satisfies. Overlapping groups are
    // allowed (one piece can sit in several), so a piece can pick up more than one line.
    private void AddCategoriesToGear()
    {
        foreach (var (gearGroupName, gearGroup) in Settings.GearCategories)
        {
            string gearGroupLabel = QuestLocaleGeneration.GetGearDisplayName(gearGroupName);
            foreach (var itemId in gearGroup.Items)
            {
                AddGroupLineToItem(gearGroupLabel, itemId);
            }
        }
    }

    // The mod-side counterpart of AddCategoriesToWeapons: stamps every attachment with the mod
    // categories it belongs to, so a player inspecting a silencer can see it satisfies "Big
    // Silencers" without cross-referencing the category sheet - exactly what weapons already got.
    private void AddCategoriesToMods()
    {
        foreach (var (categoryName, modGroup) in Settings.WeaponCategories.ModCategories)
        {
            foreach (var modId in modGroup.Mods)
            {
                AddGroupLineToItem(categoryName, modId);
            }
        }
    }

    // Public so other mods can call it through QuestrandomizerCompatibility-style integration - e.g. a
    // shiny version of a hand-listed AK inheriting the same "Part of category: [AKs]" text the real one
    // carries, even though it was never itself listed. Deliberately skips SELECTOR-based categories:
    // there, equivalence is not membership (a real member joins by matching the selector directly), so
    // stamping an equivalent would falsely claim e.g. a 9x19 gun is "Part of category: [5.56]".
    public void AddCategoriesToWeapon(MongoId originalId, MongoId equivalentId)
    {
        foreach (var (categoryName, weaponGroup) in Settings.ResolvedCategories)
        {
            if (weaponGroup.SkipsEquivalence)
            {
                continue;
            }
            if (weaponGroup.Weapons.Contains(originalId))
            {
                AddGroupLineToItem(categoryName, equivalentId);
                Leaves.Utils.Instance.printColor($"[Questrandomizer] Adding {categoryName} to {equivalentId}", LogTextColor.Yellow, SptLogLevel.Info, true);
            }
        }
    }

    // ============================================================
    // Global locale writes
    // ============================================================
    //
    // Everything below writes directly into the REAL, shared locale data via LazyLoad's
    // AddTransformer - unlike QuestLocaleGeneration's per-profile methods (which write into a
    // LocaleWorkspace instead), because this content is the same for every player. AddTransformer
    // re-runs its function every single time the locale is actually read (it re-parses the locale
    // file from disk first, then replays every registered transformer on top - see LazyLoad.cs) -
    // that sounds wasteful but it means these functions are safe to call more than once (or to
    // read/append inside the transformer, like AddGroupLineToItem does below) without ever
    // double-appending text: every read starts from the same pristine on-disk description, so the
    // result is identical every time, no matter how many times it's actually been read before.

    private void AddLocaleTransformer(string language, Action<Dictionary<string, string>> mutate)
    {
        if (!databaseService.GetLocales().Global.TryGetValue(language, out var lazyLocale))
        {
            return;
        }
        lazyLocale.AddTransformer(locale =>
        {
            if (locale != null)
            {
                mutate(locale);
            }
            return locale;
        });
    }

    private void WriteLocaleToAll(string id, string text)
    {
        foreach (var language in databaseService.GetLocales().Global.Keys)
        {
            WriteLocale(language, id, text);
        }
    }

    private void WriteLocale(string language, string id, string text)
    {
        AddLocaleTransformer(language, locale => locale[id] = text);
    }

    private void WriteFullLocale(string language, string targetId, string name, string shortName, string description)
    {
        AddLocaleTransformer(language, locale =>
        {
            locale[$"{targetId} Name"] = name;
            locale[$"{targetId} ShortName"] = shortName;
            locale[$"{targetId} Description"] = description;
        });
    }

    // One entry per (item, label) pair ever registered. An item can legitimately sit in several
    // categories (each is its own "Part of category" line), but the SAME (item, label) pair must only be
    // registered once - e.g. a weapon that reaches a category both directly and via a selector match
    // would otherwise append the line twice. The transformer replay-from-pristine guarantee only dedupes
    // re-READS, not double registration, so gate at registration instead. One shared set across weapons,
    // mods and gear: the key is "{itemId}|{label}" and those never collide across kinds (a weapon, an
    // attachment and a gear piece are distinct items with distinct ids). Gear is keyed by its display
    // LABEL (what's printed), which is exactly what the other two kinds already effectively key on too.
    private readonly HashSet<string> registeredGroupLines = new();

    // Appends one "{QR} <phrase> [bracketText]" line to targetId's Description in a SINGLE language -
    // the shared mechanics behind both the "Part of category" group lines (AddGroupLineToItem) and the
    // "Equivalent to" line (AddEquivalentToWeapon). bracketText is handed in already-resolved: the
    // caller localizes it first if it needs to (see the recursive-read-lock note in
    // AddEquivalentToWeapon), so this transformer only ever touches the locale dictionary it's given and
    // never calls back into getLocale. Registration-time dedup, where a caller wants it, stays at the
    // call site.
    private void AppendDescriptionLine(string language, MongoId targetId, string phraseKey, string bracketText)
    {
        AddLocaleTransformer(language, locale =>
        {
            string key = $"{targetId} Description";
            locale.TryGetValue(key, out var desc);
            desc ??= "";
            desc += "\n";
            desc += Leaves.Utils.Instance.RTT_Bold("{QR} " + QuestLocaleGeneration.GetLoc(phraseKey));
            desc += Leaves.Utils.Instance.RTT_Italic(" [" + bracketText + "]");
            locale[key] = desc;
        });
    }

    // Appends a single "{QR} Part of category: [label]" line to itemId's in-game description in every
    // language, deduped per (item, label). Shared by weapons, mods (silencers etc.) and gear pieces -
    // the label is printed raw ("Part of category: [Big Silencers]" / "[AKs]" / "[FORT body armor]" all
    // read correctly as-is), so there's nothing kind-specific left to vary.
    private void AddGroupLineToItem(string label, MongoId itemId)
    {
        if (!registeredGroupLines.Add($"{itemId}|{label}"))
        {
            return; // this exact category line is already registered for this item
        }

        foreach (var language in databaseService.GetLocales().Global.Keys)
        {
            AppendDescriptionLine(language, itemId, "PartOf", label);
        }
    }

    private void AddEquivalentToWeapon(MongoId originalId, MongoId equivalentId)
    {
        foreach (var language in databaseService.GetLocales().Global.Keys)
        {
            // Leaves.Utils.Instance.getLocale reads through LocaleService, which is itself backed by a
            // LazyLoad - and LazyLoad.Value takes a read lock for the whole time it's running its
            // registered transformers (see AddLocaleTransformer below). Calling getLocale from
            // INSIDE a transformer means calling back into LazyLoad.Value a second time WHILE the
            // first call's lock is still held - .NET's ReaderWriterLockSlim treats that as an error
            // ("recursive read lock acquisitions not allowed") rather than just letting it through,
            // and crashed the whole server on startup the first time this ran live. So the original
            // weapon's display name has to be looked up here, BEFORE the transformer is registered,
            // and handed to AppendDescriptionLine as a plain already-resolved string - the
            // transformer itself then only touches the locale dictionary it's given, no further
            // lookups. (This per-language localized bracket text is exactly why equivalents can't just
            // reuse AddGroupLineToItem, which takes one language-agnostic label.)
            string? originalName = Leaves.Utils.Instance.getLocale(language, originalId.ToString(), " Name");

            AppendDescriptionLine(language, equivalentId, "EquivalentTo", originalName ?? "");
        }
    }
}
