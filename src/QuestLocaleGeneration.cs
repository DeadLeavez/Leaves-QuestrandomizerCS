using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
// "LogLevel" alone is ambiguous - ASP.NET Core's own Microsoft.Extensions.Logging.LogLevel is
// implicitly in scope too (this project targets Microsoft.NET.Sdk.Web), and it has different
// members (Information, not Info).
using SptLogLevel = SPTarkov.Server.Core.Models.Spt.Logging.LogLevel;

using Leaves_Questrandomizer.Pipeline;

namespace Leaves_Questrandomizer;

// Builds the human-readable sentence describing a randomized quest condition
// (e.g. "Kill 5 Scavs using [SMG] at [Customs]").
//
// THE MOD IS ENGLISH-ONLY. The sentence is built ONCE, in English, and written into the flat
// per-profile LocaleWorkspace (id -> text, no language dimension). The served locale for whatever
// language a client asks for is built lazily by GetLocaleDbPatch, which overlays these English edits
// onto that language's vanilla locale on demand - so a non-English client keeps its own menus and item
// names, only the mod's quest text is English, and no key is ever missing (a blank objective). This
// replaced a full 16-language translation pipeline (nobody translated it) and its eager per-language
// clone.
//
// Where the English words come from:
//  - Fixed connective words ("Kill", "using", "at", "wearing", "Hand over", ...) are hard-coded
//    string literals right in the sentence builders below.
//  - DATA-keyed names - map ids, enemy roles, body parts - are looked up from the English-only table
//    assets/data/localization/en/base.json (see GetLoc). These stay in a file because they are data,
//    not prose: a big keyed list that reads fine even in pure English.
//  - Item / trader names come from the game's own locale via Leaves.Utils.Instance.getLocale("en", ...).
//  - A weapon category's display name IS the category name (they were identical in every language).
//  - A gear group's label comes from the gear data file (Settings.GearCategories[...].DisplayName).
//
// Every method that WRITES locale text takes a LocaleWorkspace parameter instead of touching any
// shared/global locale data directly - see LocaleWorkspace.cs for why (per-profile generation has
// to stay self-contained so concurrent profiles' generation can never step on each other).
public static class QuestLocaleGeneration
{
    // English key -> name, for the DATA-keyed lookups only (map ids, enemy roles, body parts).
    // Loaded from assets/data/localization/en/base.json by Init.
    private static Dictionary<string, string> phrases = null!;

    // Colours for the (R)/(F) mod-requirement markers appended to a weapon group. Deliberately the
    // same greens/reds the handbook cards use for required / forbidden mods (styles.css .hbtag.req /
    // .forb), so the marker and the handbook read as one colour language.
    private const string RequiredModMarkerColor = "#7fd694";
    private const string ForbiddenModMarkerColor = "#e88a8a";

    // Loads the phrase table once. Called from Questrandomizer.OnLoad (it reads
    // Leaves.Utils.Instance). Read-only after; every method here is a pure function of its arguments + this
    // table + the static Settings, so it is safe to call from concurrent per-profile generation.
    public static void Init(string modPath)
    {
        string basePath = System.IO.Path.Combine(modPath, "assets/data/localization/en/base.json");
        if (System.IO.File.Exists(basePath))
        {
            phrases = Leaves.Utils.Instance.loadFile<Dictionary<string, string>>(basePath);
        }
        else
        {
            phrases = new Dictionary<string, string>();
        }
    }

    // English name for a DATA key (map id, enemy role, body part). Falls back to the raw key so a
    // missing entry renders visibly-untranslated in the sentence rather than crashing or blanking,
    // and tells whoever edits base.json exactly which key needs adding. (Historically this took a
    // language and did lang -> en -> key; the mod is English-only now, so it is a single lookup.)
    public static string GetLoc(string key)
    {
        if (phrases.TryGetValue(key, out var value))
        {
            return value;
        }
        return key;
    }

    // A gear group's display label, straight from the gear data file, falling back to the raw group
    // key (so a group with no label shows its key, visibly un-labelled, rather than blank).
    public static string GetGearDisplayName(string gearGroup)
    {
        if (Settings.GearCategories.TryGetValue(gearGroup, out var entry) && entry.DisplayName is { Length: > 0 } label)
        {
            return label;
        }
        return gearGroup;
    }

    // TEXT FOR THE BUILT QUEST - the whole quest's objective text, driven by the stored decisions
    // LAYERED OVER the quest they were just applied to.
    //
    // `quest` is the REGENERATED quest (every caller runs QuestRegeneration.Regenerate immediately
    // before this), and it is load-bearing, not incidental. The outline is SPARSE: it holds decisions
    // only, so an objective whose map was rerolled has a `maps` entry and nothing else - while its
    // vanilla time window, body parts, boss target, count and weapons are all still live requirements
    // on the built quest. Rendering the outline's fields alone therefore said nothing about any of
    // them (a boss quest with a rolled weapon category read "Kill 0 using [AKs] at [Any map]").
    //
    // So every mechanical value below is read through ObjectiveFacts - "the decision if there was one,
    // else what the quest actually says" - and the outline is consulted directly only for the things
    // that exist NOWHERE else: weapon category NAMES, the gear group LABEL and the handover category
    // NAME (the built quest has those expanded into id lists, which is not what a player wants to read).
    //
    // Reading the regenerated quest rather than pristine vanilla is deliberate: regeneration rebuilds
    // some fields wholesale from the outline (kill Distance is rewritten every time, so a dropped
    // distance really is gone) while leaving others vanilla. It is the only quest that says what the
    // game will really check - see ObjectiveFacts for the full two-callers note.
    public static void GenerateFromOutline(QuestOutline outline, Quest quest, LocaleWorkspace workspace)
    {
        foreach (var objective in outline.Objectives)
        {
            if (objective.Type == ObjectiveType.Kills && objective.Kills is { } kills)
            {
                GenerateKillsLocaleFromOutline(workspace, objective, kills, quest);
            }
            else if (objective.Type == ObjectiveType.Handover && objective.Handover is { } handover)
            {
                GenerateHandoverLocaleFromOutline(workspace, objective, handover, quest);
            }
        }

        // Handover objectives the roller left UNTOUCHED still get their line rewritten (it restates
        // the vanilla requirement in this mod's phrasing). They have no decisions, so they never
        // appear in the outline - they have to be picked up from the quest itself.
        var described = new HashSet<MongoId>();
        foreach (var objective in outline.Objectives)
        {
            described.Add(objective.ConditionId);
        }
        foreach (var task in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
        {
            if (task.ConditionType == "HandoverItem"
                && !described.Contains(task.Id)
                && task.Target?.List?.Count == 1)
            {
                GenerateHandoverItemLocale(workspace, task, "");
            }
        }
    }

    // SECOND locale pass, over the FULL served quest table, run AFTER all per-outline/procedural
    // generation: give every objective a searchable "[Map]" suffix UNLESS one was already written this
    // run. A rolled kill already renders its maps (GenerateKillsLocaleFromOutline) and its condition id
    // is therefore a key in workspace.Changes - so skipping ids already in Changes is exactly "don't
    // touch objectives we edited", at objective granularity, and it needs no roller bookkeeping. What's
    // left is the gap the outline pass never covers: FIND objectives (never generated) and every
    // objective on a quest served vanilla (whitelist-skipped / unrolled). Annotating those is what lets
    // an unrolled kill's map be searched, and a location-less one be caught by the any-map filter, both
    // in-game (via GetLocaleDbPatch overlaying workspace) and in the web UI (ResolveObjectives reads the
    // same edits). Deliberately runs on profileQuests, not just the rolled subset.
    public static void AppendMapSuffixes(Dictionary<MongoId, Quest> servedQuests, LocaleWorkspace workspace)
    {
        foreach (var (_, quest) in servedQuests)
        {
            foreach (var task in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
            {
                string conditionId = task.Id.ToString();
                if (workspace.Changes.ContainsKey(conditionId))
                {
                    continue; // already generated this run (rolled kill / handover / procedural) - leave it
                }

                List<string>? maps = QuestFacts.GetObjectiveMapDisplays(task);
                if (maps == null)
                {
                    continue; // not a map-annotated objective type (handover, plant/zone-only, ...)
                }

                string baseText = Leaves.Utils.Instance.getLocale("en", conditionId) ?? "";
                if (baseText.Length == 0)
                {
                    continue; // no vanilla text to annotate
                }

                // A specific map the vanilla sentence already spells out in prose needs no bracket - it's
                // already searchable, and "...on Customs [Customs]" would just read badly.
                if (maps.Count > 0 && TextNamesAllMaps(baseText, maps))
                {
                    continue;
                }

                string inner = maps.Count > 0 ? JoinOr(maps) : "Any map";
                workspace.Edit(conditionId, baseText + $" [{inner}]");
            }
        }
    }

    // True when the objective text already names every one of these maps (case-insensitive), i.e. the
    // vanilla sentence spells the location out, so a bracket would only duplicate it.
    private static bool TextNamesAllMaps(string text, List<string> maps)
    {
        string haystack = text.ToLowerInvariant();
        foreach (string map in maps)
        {
            if (!haystack.Contains(map.ToLowerInvariant()))
            {
                return false;
            }
        }
        return true;
    }

    // ONE kill objective's sentence, built ONCE in English from what the objective ACTUALLY requires:
    // each value read through ObjectiveFacts (decision first, built quest second) rather than off the
    // sparse outline, so a requirement the roll never touched still gets described. The things no fact
    // can answer - each weapon category's live mod-requirement flags, the Ground Zero map collapse, and
    // the gear - are derived here as locals.
    //
    // Gear is read off the built condition rather than through a fact: this mod never rolls FORBIDDEN
    // gear (it is vanilla's own, and only lives on the quest), and REQUIRED gear is already written
    // there by the structure pass - vanilla's own wherever gear wasn't re-rolled.
    private static void GenerateKillsLocaleFromOutline(LocaleWorkspace workspace, ObjectiveOutline objective, KillsOutline kills, Quest quest)
    {
        // The category NAMES required.
        var weaponCategories = new List<string>();
        if (kills.WeaponCategories is { Count: > 0 } categories)
        {
            weaponCategories = categories;
        }

        // Whether ANY rolled category carries required and/or forbidden mod groups - looked up LIVE (so
        // retuning a category reaches old quests). A multi-category roll uses only mod-less categories,
        // so both stay false for those. We only need the booleans: the objective line shows a compact
        // (R)/(F) marker, not the mod list (the handbook, linked off the category name, lists the mods).
        bool anyRequiredMods = false;
        bool anyForbiddenMods = false;
        foreach (var category in weaponCategories)
        {
            if (Settings.ResolvedCategories.TryGetValue(category, out var resolved))
            {
                if (resolved.RequiredMods.Count > 0)
                {
                    anyRequiredMods = true;
                }
                if (resolved.ForbiddenMods.Count > 0)
                {
                    anyForbiddenMods = true;
                }
            }
        }

        // The DISPLAY names of the maps IN EFFECT (rolled maps, else the quest's own Location condition),
        // through the one shared collapse (QuestFacts.MapDisplaysFromCodes): Ground Zero's two backend
        // maps read as one, and a factory DAY objective widened to also count at night reads as a single
        // "[Factory]" - the same rule the bracket-suffix path uses, so both surfaces agree. Empty when the
        // objective has no Location at all (the "any map" branch below).
        List<string> mapDisplays = QuestFacts.MapDisplaysFromCodes(ObjectiveFacts.GetMaps(quest, objective));

        // Weapons rendered as WEAPONS rather than as a category name: a rolled pinned set (mystery
        // arsenal), or - when the roll named no weapons at all - whatever weapons the quest itself
        // pins. Empty when categories were rolled, since those render by name just below.
        List<string> pinnedWeaponIds = ObjectiveFacts.GetPinnedWeapons(quest, objective);

        // Boss/role hunt uses the role name(s) as the target; otherwise a plain target key. The roller
        // never DECIDES a savage role, so this is effectively always the quest's own - which is exactly
        // why it has to be read as a fact: taken off the outline it was always empty, and every boss
        // quest silently lost its target.
        var savageRoles = new List<string>();
        foreach (string role in ObjectiveFacts.GetSavageRoles(quest, objective))
        {
            savageRoles.Add(role.ToLowerInvariant());
        }
        bool savageHunt = savageRoles.Count > 0;
        string targetKey = savageHunt ? "" : (ObjectiveFacts.GetTarget(quest, objective) ?? "").ToLowerInvariant();

        // Distance/daytime are read as flat values; wrap them back into the shapes the sentence reads
        // (and treat an all-zero daytime as "not set", matching the roller).
        CounterConditionDistance? distance = null;
        double? distanceValue = ObjectiveFacts.GetDistanceValue(quest, objective);
        if (distanceValue > 0)
        {
            distance = new CounterConditionDistance
            {
                CompareMethod = ObjectiveFacts.GetDistanceCompare(quest, objective),
                Value = distanceValue
            };
        }
        DaytimeCounter? daytime = null;
        (int From, int To) window = ObjectiveFacts.GetDaytime(quest, objective);
        if (!(window.From == 0 && window.To == 0))
        {
            daytime = new DaytimeCounter { From = window.From, To = window.To };
        }

        List<string> bodyParts = ObjectiveFacts.GetBodyParts(quest, objective);
        double killCount = ObjectiveFacts.GetKillCount(quest, objective) ?? 0;

        // BOTH gear lists come from the built quest, not the outline (see ObjectiveFacts.GetGear). Null
        // required-gear means "nothing to describe".
        var (gearGroups, forbiddenGearGroups) = ObjectiveFacts.GetGear(quest, objective);

        // Build the English sentence ONCE.
        var parts = new List<string> { KillTargetText(savageRoles, killCount, targetKey) };

        if (distance != null)
        {
            parts.Add($"at {distance.CompareMethod} {distance.Value}m");
        }
        // The roll never ADDS a time window on labs/factory, but vanilla is free to have put one there
        // and regeneration keeps it - so this renders whenever a window is actually in effect.
        if (daytime != null)
        {
            parts.Add($"during {(daytime.From ?? 0):00}-{(daytime.To ?? 0):00}");
        }
        if (weaponCategories.Count > 0 || pinnedWeaponIds.Count > 0)
        {
            // Tag the weapon group with a compact coloured marker when its category carries mod rules:
            // green (R) if it REQUIRES mods, red (F) if it FORBIDS some (both can show, e.g. "(R)(F)").
            // The colour is EFT rich text for the in-game journal; the web UI strips rich text and
            // re-colours the plain (R)/(F) tokens itself (see colorModMarkers in questrandomizer.html). Which
            // specific mods a category requires/forbids lives in the handbook, linked off the category
            // name - so the objective line stays short instead of spelling the whole list.
            string weaponText = WeaponNameText(weaponCategories, pinnedWeaponIds);
            if (anyRequiredMods)
            {
                weaponText += Leaves.Utils.Instance.RTT_Color("(R)", RequiredModMarkerColor);
            }
            if (anyForbiddenMods)
            {
                weaponText += Leaves.Utils.Instance.RTT_Color("(F)", ForbiddenModMarkerColor);
            }
            parts.Add($"using {weaponText}");
        }
        if (bodyParts.Count > 0)
        {
            var localizedParts = new List<string>();
            foreach (var part in bodyParts)
            {
                localizedParts.Add(GetLoc(part));
            }
            parts.Add($"in: [{JoinOr(localizedParts)}]");
        }
        if (mapDisplays.Count > 0)
        {
            parts.Add($"at [{JoinOr(mapDisplays)}]");
        }
        else
        {
            // No maps => the quest has no Location condition at all (see the rolled path for the full
            // rationale), so it really does count everywhere. Saying so explicitly beats an absent
            // line, which reads as a text bug.
            parts.Add("at [Any map]");
        }
        // Required gear: a rolled gear GROUP renders its label ("[FORT body armor]"), reading cleanly -
        // the individual pieces are in the group, not the objective line. A legacy pinned-item outline
        // (no group name yet, converted on the next boot by OutlineVerification) still renders its item
        // list the old way.
        if (kills.RequiredGearCategory is { } gearGroupName)
        {
            parts.Add($"wearing: [{GetGearDisplayName(gearGroupName)}]");
        }
        else if (gearGroups != null)
        {
            parts.Add("wearing: " + GearText(gearGroups));
        }
        if (forbiddenGearGroups.Count > 0)
        {
            parts.Add("not wearing: " + GearText(forbiddenGearGroups));
        }

        string line = string.Join(" ", parts);
        workspace.Edit(objective.ConditionId.ToString(), line);

        Leaves.Utils.Instance.printColor(line, LogTextColor.Magenta, SptLogLevel.Info, true);
    }

    // Same rule as the kill sentence: count, found-in-raid and the item are the values IN EFFECT (the
    // decision if there was one, else the built quest's), because a handover objective the roll only
    // partly touched - a rerolled item but no count decision, say - still has a real count on it. The
    // category NAME is outline-only: the built quest holds the category expanded into an item list,
    // which is not something to read back to a player.
    private static void GenerateHandoverLocaleFromOutline(LocaleWorkspace workspace, ObjectiveOutline objective, HandoverOutline handover, Quest quest)
    {
        string line = $"Hand over {ObjectiveFacts.GetHandoverCount(quest, objective) ?? 0} ";

        if (ObjectiveFacts.GetFoundInRaid(quest, objective))
        {
            string firText = "found in raid ";
            line += Settings.Config.RainbowifyFIR ? Leaves.Utils.Instance.RTT_Rainbowify(firText) : Leaves.Utils.Instance.RTT_TFify(firText);
        }

        if (handover.CategoryName is { Length: > 0 } categoryName)
        {
            string newName = Leaves.Utils.Instance.getLocale("en", categoryName, " Name")
                ?? GetLoc($"ITEMCATEGORY_{categoryName}");
            line += $"items from the {newName} Category ";
        }
        else
        {
            string? itemId = ObjectiveFacts.GetHandoverItem(quest, objective);
            string? itemName = itemId != null ? Leaves.Utils.Instance.getLocale("en", itemId, " Name") : null;
            line += string.IsNullOrEmpty(itemName) ? "" : $"[{itemName}]";
        }

        workspace.Edit(objective.ConditionId.ToString(), line);
    }

    // "Kill 18 pmcs" / "Kill 10 marksman" - boss/role hunts use the role name(s) as the target.
    private static string KillTargetText(List<string> savageRoles, double kills, string targetKey)
    {
        string target;
        if (savageRoles.Count > 0)
        {
            var roleNames = new List<string>();
            foreach (var role in savageRoles)
            {
                roleNames.Add(GetLoc(role));
            }
            target = string.Join(" ", roleNames);
        }
        else
        {
            target = GetLoc(targetKey);
        }

        // Join-and-filter instead of plain interpolation so a blank target can't leave a double
        // space in the middle of the sentence.
        var tokens = new List<string>();
        foreach (var token in new[] { "Kill", kills.ToString(), target })
        {
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }
        return string.Join(" ", tokens);
    }

    private static string WeaponNameText(List<string> weaponCategories, List<string> pinnedWeaponIds)
    {
        // A pinned weapon set has no category name to point at - render the actual weapons,
        // ShortNames to keep three guns from swallowing the whole objective line.
        if (pinnedWeaponIds.Count > 0)
        {
            var shortNames = new List<string>();
            foreach (var id in pinnedWeaponIds)
            {
                shortNames.Add(Leaves.Utils.Instance.getLocale("en", id, " ShortName") ?? id);
            }
            return "[" + JoinOr(shortNames) + "]";
        }

        // One or more category names joined with " or " ("[AKs]", or "[AKs or SMGs]" for a multi-roll).
        // A weapon category's display name IS its name now (they were identical in every shipped
        // language). Bracketed like every other tag in the sentence (maps, mods, body parts) - it
        // unifies the look and lets the web UI's linkify pick the category name out and link it to the
        // handbook, same as it does for items.
        return "[" + JoinOr(weaponCategories) + "]";
    }

    // Deliberately a literal English " or " - the original hardcoded it too.
    private static string JoinOr(IEnumerable<string> items)
    {
        return string.Join(" or ", items);
    }

    // "[helm + armor] or [other helm]" - gear worn TOGETHER shares brackets, joined with " + ";
    // alternative loadouts are separated by " or ". Returns just the bracketed body; the caller prefixes
    // the intro ("wearing: " for required gear, "not wearing: " for forbidden).
    //
    // Capped: some vanilla forbidden-gear lists are ENORMOUS (Swift One forbids 118 individual items,
    // Unprotected but Dangerous 60) - rendering them all would drown the objective line, so anything
    // past the cap collapses into a "(+N more)" tail.
    private static string GearText(List<List<string>> gearGroups)
    {
        // Beyond a certain size the list stops being useful to enumerate at all - "don't wear armor"
        // quests forbid basically every armour/rig in the game, and even a "(+83 more)" tail is just
        // noise. Once past this threshold, collapse the whole thing to a single "[Any armor]" instead.
        // Required-gear lists are always small curated groups, so in practice this only ever fires on
        // the giant vanilla FORBIDDEN lists.
        const int maxGroupsBeforeCollapse = 50;
        if (gearGroups.Count > maxGroupsBeforeCollapse)
        {
            return "[Any armor]";
        }

        // Vanilla data often stores "either of these armors PLUS either of these helmets" as the full
        // cartesian product of combos (Punisher Part 5: 2 armors x 2 helmets = 4 two-item groups).
        // Rendered raw that's a wall of repeated names - factoring it back to one bracket per SLOT
        // reads far better: "[armor A or armor B] + [helmet C or helmet D]".
        var factoredSlots = TryFactorGearGroups(gearGroups);
        if (factoredSlots != null)
        {
            var slots = new List<string>();
            foreach (var alternatives in factoredSlots)
            {
                var names = new List<string>();
                foreach (var id in alternatives)
                {
                    names.Add(Leaves.Utils.Instance.getLocale("en", id, " Name") ?? id);
                }
                slots.Add("[" + JoinOr(names) + "]");
            }
            return string.Join(" + ", slots);
        }

        const int maxGroupsShown = 6;
        var groups = new List<string>();
        int shown = 0;
        foreach (var group in gearGroups)
        {
            if (shown >= maxGroupsShown)
            {
                break;
            }
            var names = new List<string>();
            foreach (var id in group)
            {
                names.Add(Leaves.Utils.Instance.getLocale("en", id, " Name") ?? id);
            }
            groups.Add("[" + string.Join(" + ", names) + "]");
            shown++;
        }
        string text = JoinOr(groups);

        int hiddenCount = gearGroups.Count - maxGroupsShown;
        if (hiddenCount > 0)
        {
            text += $" (+{hiddenCount} more)";
        }
        return text;
    }

    // Detects whether gearGroups is exactly the cartesian product of per-slot alternatives, and if so
    // returns those alternatives (one list per slot). Works by collecting the distinct items at each
    // position across all groups: if (distinct at slot 0) x (distinct at slot 1) x ... multiplies out
    // to exactly the group count AND every single combination actually appears in the data, the data
    // IS a clean product and can be shown factored. Anything irregular - mixed group lengths, missing
    // combos, duplicates - returns null and the caller renders the groups as-is.
    private static List<List<string>>? TryFactorGearGroups(List<List<string>> gearGroups)
    {
        if (gearGroups.Count < 2)
        {
            return null;
        }
        int slotCount = gearGroups[0].Count;
        if (slotCount < 2 || gearGroups.Any(group => group.Count != slotCount))
        {
            return null;
        }

        var slots = new List<List<string>>();
        long combinations = 1;
        for (int slot = 0; slot < slotCount; slot++)
        {
            var alternatives = gearGroups.Select(group => group[slot]).Distinct().ToList();
            slots.Add(alternatives);
            combinations *= alternatives.Count;
        }
        if (combinations != gearGroups.Count)
        {
            return null;
        }

        // Counts match - now make sure every combination is really present (a same-sized set of groups
        // could coincidentally have matching distinct-counts without being a product).
        var presentGroups = new HashSet<string>(gearGroups.Select(group => string.Join("", group)));
        if (presentGroups.Count != gearGroups.Count)
        {
            return null; // duplicate groups - not a clean product
        }
        var current = new string[slotCount];
        bool AllCombosPresent(int slot)
        {
            if (slot == slotCount)
            {
                return presentGroups.Contains(string.Join("", current));
            }
            foreach (var alternative in slots[slot])
            {
                current[slot] = alternative;
                if (!AllCombosPresent(slot + 1))
                {
                    return false;
                }
            }
            return true;
        }
        return AllCombosPresent(0) ? slots : null;
    }

    public static void GenerateHandoverItemLocale(LocaleWorkspace workspace, QuestCondition task, string categoryName)
    {
        string line = $"Hand over {task.Value} ";

        if (task.OnlyFoundInRaid == true)
        {
            string firText = "found in raid ";
            line += Settings.Config.RainbowifyFIR ? Leaves.Utils.Instance.RTT_Rainbowify(firText) : Leaves.Utils.Instance.RTT_TFify(firText);
        }

        if (categoryName == "")
        {
            string? itemId = task.Target?.List?.FirstOrDefault() ?? task.Target?.Item;
            // Bracket the item name, the same way gear/weapon/map choices are already written
            // ("[armor A or armor B]", "at [Woods]"). Reads consistently in the journal AND lets the
            // re-roll web UI turn it into a wiki link. The category branch below is left alone -
            // "items from the X category" names a category, not a specific item.
            string? itemName = itemId != null ? Leaves.Utils.Instance.getLocale("en", itemId, " Name") : null;
            line += string.IsNullOrEmpty(itemName) ? "" : $"[{itemName}]";
        }
        else
        {
            string newName = Leaves.Utils.Instance.getLocale("en", categoryName, " Name")
                ?? GetLoc($"ITEMCATEGORY_{categoryName}");
            line += $"items from the {newName} Category ";
        }

        workspace.Edit(task.Id.ToString(), line);
    }
}
