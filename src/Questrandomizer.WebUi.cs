using System.IO.Compression;
using System.Reflection;
using Leaves_Questrandomizer.Patches;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

using Leaves_Questrandomizer.Pipeline;

namespace Leaves_Questrandomizer;

// Web-UI data layer (split out of Questrandomizer.cs - same partial class). Everything the reroll /
// handbook / debug HTTP API (QuestrandomizerRerollApi) reads or drives: the query views, the reroll
// actions, the web DTO records, and the helpers used only by them. Feature layer: it calls INTO core
// generation and the cache layer; neither ever calls back into here (see HANDOFF for the rule).
public partial class Questrandomizer
{
    // ========================================================================================
    // Runtime re-roll (see REROLL_SPEC.md). Phase 1: re-roll the randomization of a profile's
    // currently-accepted, uncompleted, vanilla-edited quests.
    // ========================================================================================

    // Image is the vanilla quest icon path (e.g. "/files/quest/icon/<hash>.jpg") - randomization
    // never changes it. The game server serves it on the same origin, so the web page can use it
    // as an <img src> directly.
    public sealed record RerollableQuest(MongoId QuestId, string Name, string? Image, string Trader,
        string? TraderAvatar,
        List<Questrandomizer.ObjectiveDetail> Objectives, List<QuestLink> Previous, List<QuestLink> LeadsTo,
        List<Questrandomizer.RewardDetail> Rewards);

    // ---- Handbook (web UI) --------------------------------------------------------------------------
    // The weapon + mod categories quest generation draws from - the same content QuestHandbookGeneration
    // puts in the in-game handbook, served to the web page's separate handbook tab. Shared across all
    // profiles (there is no player concept here), so no profileId; names resolve to English display text
    // like the rest of the page.
    public sealed record HandbookData(
        List<HandbookWeaponCategory> WeaponCategories,
        List<HandbookModCategory> ModCategories,
        List<HandbookGearCategory> GearCategories,
        List<HandbookWeapon> AllWeapons);
    public sealed record HandbookWeaponCategory(string Name, bool AnyWeapon, List<HandbookWeapon> Weapons, List<string> RequiredMods, List<string> ForbiddenMods);
    // One weapon in a category, plus every OTHER weapon that counts as it for a quest (its equivalents) -
    // the same "= <name>" lines the in-game sheet indents under a weapon. Equivalents that are themselves
    // listed members of the same category are dropped, so nothing shows up twice.
    public sealed record HandbookWeapon(string Name, List<string> Equivalents);
    public sealed record HandbookModCategory(string Name, List<string> Mods);
    // One gear group a "wear X" objective can require: its translated label (the SAME text that appears
    // bracketed in the objective, so the re-roll page can deep-link it) plus the item names it accepts.
    public sealed record HandbookGearCategory(string Name, List<string> Items);

    public HandbookData GetHandbookData()
    {
        const string language = "en";

        // original -> everything that TRANSITIVELY counts as it, matching the in-game sheet. Only used
        // for hand-listed/mixed categories below; a purely selector-based category shows members only.
        var equivalentsByOriginal = transitiveWeaponEquivalents;

        var weaponCategories = new List<HandbookWeaponCategory>();
        foreach (var (categoryName, resolved) in Settings.ResolvedCategories)
        {
            // For a hand-listed/mixed category, each member carries its equivalents (a shiny of a listed
            // gun counts, so it's shown). A PURELY selector-based category (SkipsEquivalence) lists members
            // only: equivalence doesn't apply, and a shiny/modded gun that belongs is already a member in
            // its own right. An equivalent that's already a listed member is skipped so nothing lists twice.
            var members = new HashSet<MongoId>(resolved.Weapons);
            var weapons = new List<HandbookWeapon>();
            foreach (var weaponId in resolved.Weapons)
            {
                string? name = Leaves.Utils.Instance.getLocale(language, weaponId.ToString(), " Name");

                var equivalents = new List<string>();
                if (!resolved.SkipsEquivalence && equivalentsByOriginal.TryGetValue(weaponId, out var equivalentIds))
                {
                    foreach (var equivalentId in equivalentIds)
                    {
                        if (members.Contains(equivalentId))
                        {
                            continue;
                        }
                        string? equivalentName = Leaves.Utils.Instance.getLocale(language, equivalentId.ToString(), " Name");
                        equivalents.Add(equivalentName ?? equivalentId.ToString());
                    }
                }
                equivalents.Sort(StringComparer.OrdinalIgnoreCase);

                weapons.Add(new HandbookWeapon(name ?? weaponId.ToString(), equivalents));
            }
            weapons.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // A weapon category's display name IS its name now (English-only; the per-language display
            // labels were identical to the key in every shipped locale).
            string display = categoryName;
            weaponCategories.Add(new HandbookWeaponCategory(
                display, resolved.AnyWeapon, weapons,
                new List<string>(resolved.RequiredMods), new List<string>(resolved.ForbiddenMods)));
        }
        weaponCategories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var modCategories = new List<HandbookModCategory>();
        foreach (var (categoryName, modEntry) in Settings.WeaponCategories.ModCategories)
        {
            var mods = new List<string>();
            foreach (var modId in modEntry.Mods)
            {
                string? name = Leaves.Utils.Instance.getLocale(language, modId.ToString(), " Name");
                mods.Add(name ?? modId.ToString());
            }
            mods.Sort(StringComparer.OrdinalIgnoreCase);
            modCategories.Add(new HandbookModCategory(categoryName, mods));
        }
        modCategories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // Gear groups the "wear X" objectives roll over. Name is the group's translated label (what the
        // objective text shows, so the re-roll page's linkify can match it); Items are the interchangeable
        // pieces, resolved to display names and sorted like the weapon lists above.
        var gearCategories = new List<HandbookGearCategory>();
        foreach (var (groupName, group) in Settings.GearCategories)
        {
            var items = new List<string>();
            foreach (var itemId in group.Items)
            {
                string? name = Leaves.Utils.Instance.getLocale(language, itemId.ToString(), " Name");
                items.Add(name ?? itemId.ToString());
            }
            items.Sort(StringComparer.OrdinalIgnoreCase);

            string display = QuestLocaleGeneration.GetGearDisplayName(groupName);
            gearCategories.Add(new HandbookGearCategory(display, items));
        }
        gearCategories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new HandbookData(weaponCategories, modCategories, gearCategories, BuildAllWeaponsList());
    }

    // Flat, deduplicated list of every weapon the randomizer can require - the union of all category
    // members - each with its FULL transitive equivalents (the weapons/reskins that also count as it for
    // a quest). Unlike the per-category weapon lists above, equivalents here are NOT dropped for sharing
    // a category: this is a global "weapon -> what counts as it" view, so every relationship is shown.
    // Powers the handbook page's "All weapons" tab.
    private List<HandbookWeapon> BuildAllWeaponsList()
    {
        const string language = "en";
        var equivalentsByOriginal = transitiveWeaponEquivalents;

        // Union every category's members into one id set, so a gun that sits in several categories is
        // listed exactly once.
        var weaponIds = new HashSet<MongoId>();
        foreach (var resolved in Settings.ResolvedCategories.Values)
        {
            foreach (var weaponId in resolved.Weapons)
            {
                weaponIds.Add(weaponId);
            }
        }

        var weapons = new List<HandbookWeapon>();
        foreach (var weaponId in weaponIds)
        {
            string? name = Leaves.Utils.Instance.getLocale(language, weaponId.ToString(), " Name");

            var equivalents = new List<string>();
            if (equivalentsByOriginal.TryGetValue(weaponId, out var equivalentIds))
            {
                foreach (var equivalentId in equivalentIds)
                {
                    string? equivalentName = Leaves.Utils.Instance.getLocale(language, equivalentId.ToString(), " Name");
                    equivalents.Add(equivalentName ?? equivalentId.ToString());
                }
            }
            equivalents.Sort(StringComparer.OrdinalIgnoreCase);

            weapons.Add(new HandbookWeapon(name ?? weaponId.ToString(), equivalents));
        }
        weapons.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return weapons;
    }

    // Everything the web UI needs to show a profile's quests AS THE GAME SERVES THEM. Replaces the old
    // per-profile quests.json/locales.json reads (that cache is gone - the outline is the source of
    // truth now). Ensures the profile has been generated (the page can be opened for a profile that
    // hasn't loaded in-game since boot), then returns: `Served` = the profile's regenerated quests +
    // `EnLocale` = its regenerated English locale, both straight from the live context; `Outlines` = the
    // stored decisions, whose KEYS are exactly the quests this profile rolled (an empty outline still
    // counts as "rolled, left vanilla"), which is what the old code used cachedQuests.ContainsKey for.
    private (Dictionary<MongoId, Quest> Served, Dictionary<string, string>? EnLocale, Dictionary<MongoId, QuestOutline> Outlines)
        GetWebUiProfileData(MongoId profileId)
    {
        EnsureProfileDataGenerated(profileId);
        QuestrandomizerContext.TryGetQuestsForProfile(profileId, out var served);
        QuestrandomizerContext.TryGetLocaleEditsForProfile(profileId, out var enLocale);
        var outlines = LoadCachedOutlines(profileId);
        return (served ?? new Dictionary<MongoId, Quest>(), enLocale, outlines);
    }

    // For the debug web page: force-generate the profile if it hasn't loaded since boot, then hand
    // back the live per-profile quest table and English locale exactly as they sit in memory - i.e.
    // what the game client is actually served. Read-only; nothing here mutates either map.
    public (Dictionary<MongoId, Quest> Quests, Dictionary<string, string> EnLocale) GetInMemoryProfileData(MongoId profileId)
    {
        EnsureProfileDataGenerated(profileId);
        QuestrandomizerContext.TryGetQuestsForProfile(profileId, out var served);
        QuestrandomizerContext.TryGetLocaleEditsForProfile(profileId, out var enLocale);
        return (served ?? new Dictionary<MongoId, Quest>(), enLocale ?? new Dictionary<string, string>());
    }

    // For the debug web page's Outlines tab: force-generate if needed, then hand back this profile's
    // stored outlines (the sole source of truth for its randomization) keyed by quest id, plus its
    // English locale so the page can label each outline with the quest's journal name. Read-only.
    public (Dictionary<MongoId, QuestOutline> Outlines, Dictionary<string, string> EnLocale) GetOutlineDataForDebug(MongoId profileId)
    {
        EnsureProfileDataGenerated(profileId);
        var outlines = LoadCachedOutlines(profileId);
        QuestrandomizerContext.TryGetLocaleEditsForProfile(profileId, out var enLocale);
        return (outlines, enLocale ?? new Dictionary<string, string>());
    }

    // The quests this profile is allowed to re-roll RIGHT NOW: currently accepted (Started or
    // ready-to-hand-in) AND uncompleted, that this mod actually rolled, excluding procedural
    // generated quests. Names come from the profile's OWN rolled locale (what the journal shows),
    // falling back to the shared locale then the raw id.
    public List<RerollableQuest> GetRerollableActiveQuests(PmcData pmc, bool admin = false)
    {
        var result = new List<RerollableQuest>();
        if (pmc.Id is not { } profileId || pmc.Quests == null)
        {
            return result;
        }

        var (served, enLocale, outlines) = GetWebUiProfileData(profileId);
        if (!admin && outlines.Count == 0)
        {
            return result;
        }

        var proceduralIds = generatedIds.ValuesSnapshot();
        var blacklist = new HashSet<MongoId>(Settings.Config.QuestBlacklist);
        var conditionCounters = BuildConditionCounters(pmc);
        var (prereqs, leadsTo) = BuildQuestChains(served);
        var chainLocale = enLocale ?? new Dictionary<string, string>();

        foreach (var questStatus in pmc.Quests)
        {
            if (questStatus.Status != QuestStatusEnum.Started && questStatus.Status != QuestStatusEnum.AvailableForFinish)
            {
                continue; // not accepted, or already completed/failed/locked
            }
            // Procedural quests (the mod's own generated ones) are never re-rollable through this
            // path, admin or not.
            if (proceduralIds.Contains(questStatus.QId))
            {
                continue;
            }
            // Admin override lists EVERY other active quest - including blacklisted, whitelist-excluded
            // and never-rolled ones - so any of them can be force-rolled. Normal mode only offers
            // quests this mod actually rolled and didn't blacklist.
            if (!admin)
            {
                if (!outlines.ContainsKey(questStatus.QId))
                {
                    continue; // we didn't roll this one
                }
                if (blacklist.Contains(questStatus.QId))
                {
                    continue; // explicitly protected from editing - don't offer to re-roll it
                }
            }

            string name = enLocale != null && enLocale.TryGetValue($"{questStatus.QId} name", out var rolledName)
                ? rolledName
                : (Leaves.Utils.Instance.getLocale("en", questStatus.QId.ToString(), " name") ?? questStatus.QId.ToString());

            // The SERVED copy is what the player sees (already holds every quest, rolled or vanilla);
            // fall back to the raw vanilla definition only if the profile somehow isn't in the context.
            if (!served.TryGetValue(questStatus.QId, out var quest)
                && !databaseService.GetQuests().TryGetValue(questStatus.QId, out quest))
            {
                continue;
            }

            result.Add(new RerollableQuest(questStatus.QId, Leaves.Utils.Instance.RTT_Strip(name), quest.Image,
                Leaves.Utils.Instance.getTraderNickname(quest.TraderId),
                Leaves.Utils.Instance.getTraderAvatar(quest.TraderId),
                ResolveObjectives(quest, enLocale, questStatus.CompletedConditions, conditionCounters),
                BuildQuestLinks(prereqs.TryGetValue(questStatus.QId, out var p) ? p : new List<MongoId>(), chainLocale),
                BuildQuestLinks(leadsTo.TryGetValue(questStatus.QId, out var l) ? l : new List<MongoId>(), chainLocale),
                ResolveRewards(quest)));
        }

        return result;
    }

    // The resolved, plain-text description for ONE quest in a profile. Lazily fetched by the web UI when
    // the user expands a quest's description - kept OFF the bulk quest payload, which would otherwise
    // carry every active quest's full description text. Prefers the profile's OWN rolled locale (a
    // re-rolled quest has a rewritten description), falls back to the shared vanilla locale.
    public string GetQuestDescription(PmcData pmc, MongoId questId)
    {
        if (pmc.Id is not { } profileId)
        {
            return "";
        }
        var (_, enLocale, _) = GetWebUiProfileData(profileId);
        string? description = null;
        if (enLocale != null && enLocale.TryGetValue($"{questId} description", out var rolled))
        {
            description = rolled;
        }
        description ??= Leaves.Utils.Instance.getLocale("en", questId.ToString(), " description");
        return Leaves.Utils.Instance.RTT_Strip(description ?? "");
    }

    // One outstanding turn-in (HandoverItem objective) on one of a profile's active quests: the item(s)
    // it accepts (usually one; a list means "any of these"), whether they must be Found-in-Raid, and how
    // many are STILL needed after subtracting whatever the player has already contributed. Feeds the Info
    // page's cross-player shopping list. No player field - the caller tags each with the owning profile.
    public sealed record TurnInNeed(string QuestName, List<string> ItemIds, List<string> ItemNames, bool Fir, int Remaining);

    // Every unfinished turn-in across a profile's ACTIVE quests (vanilla, rolled, and procedural alike -
    // a turn-in is a turn-in). "Already handed in" is honoured two ways: a fully-completed objective is
    // dropped, and a partially-counted one has its live counter subtracted from the required amount, so
    // the number shown is only what the player still has to bring. Category handovers ("items from the X
    // category") carry no concrete item id, so there is nothing to put on a shopping list - skipped.
    public List<TurnInNeed> GetTurnInNeedsForProfile(PmcData pmc)
    {
        var result = new List<TurnInNeed>();
        if (pmc.Id is not { } profileId || pmc.Quests == null)
        {
            return result;
        }

        var (served, enLocale, _) = GetWebUiProfileData(profileId);
        var conditionCounters = BuildConditionCounters(pmc);
        var itemDb = databaseService.GetItems();

        foreach (var questStatus in pmc.Quests)
        {
            if (questStatus.Status != QuestStatusEnum.Started && questStatus.Status != QuestStatusEnum.AvailableForFinish)
            {
                continue; // not accepted, or already completed/failed/locked
            }
            // The SERVED copy is what the player sees (holds every quest, rolled or vanilla); fall back to
            // the raw vanilla definition only if the profile somehow isn't in the context.
            if (!served.TryGetValue(questStatus.QId, out var quest)
                && !databaseService.GetQuests().TryGetValue(questStatus.QId, out quest))
            {
                continue;
            }

            string questName = enLocale != null && enLocale.TryGetValue($"{questStatus.QId} name", out var rolledName)
                ? rolledName
                : (Leaves.Utils.Instance.getLocale("en", questStatus.QId.ToString(), " name") ?? questStatus.QId.ToString());
            questName = Leaves.Utils.Instance.RTT_Strip(questName);

            foreach (var condition in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
            {
                if (condition.ConditionType != "HandoverItem")
                {
                    continue;
                }
                if (!TryGetShoppableHandover(condition, itemDb, out var itemIds, out var itemNames))
                {
                    continue; // category handover, or every accepted item is a quest item - nothing to shop for
                }

                string conditionId = condition.Id.ToString();
                double target = condition.Value.HasValue && condition.Value.Value > 0 ? condition.Value.Value : 1;

                // Same completion rule the objective list uses: trust a live counter when there is one,
                // else the binary CompletedConditions flag (see ResolveObjectives for why).
                double current = 0;
                bool completed;
                if (conditionCounters.TryGetValue(conditionId, out var counted))
                {
                    current = counted;
                    completed = counted >= target;
                }
                else
                {
                    completed = questStatus.CompletedConditions?.Contains(conditionId) == true;
                }
                if (completed)
                {
                    continue; // already handed in
                }
                int remaining = (int)Math.Ceiling(target - current);
                if (remaining <= 0)
                {
                    continue;
                }

                result.Add(new TurnInNeed(questName, itemIds, itemNames, condition.OnlyFoundInRaid == true, remaining));
            }
        }
        return result;
    }

    // Turns ONE HandoverItem condition into the concrete, SHOPPABLE items it accepts: the target item
    // id(s) with quest items (mission pickups flagged QuestItem, which can't be bought/bartered/crafted)
    // stripped out, plus their resolved English names. Returns false - nothing to put on a shopping
    // list - for a category handover (no concrete item id) or a condition whose every option is a quest
    // item. Shared by the live turn-in list and the debug all-hand-ins view so both exclude the same set.
    private bool TryGetShoppableHandover(
        QuestCondition condition,
        Dictionary<MongoId, TemplateItem> itemDb,
        out List<string> itemIds,
        out List<string> itemNames)
    {
        itemIds = new List<string>();
        itemNames = new List<string>();

        var rawIds = condition.Target?.List
            ?? (condition.Target?.Item != null ? new List<string> { condition.Target.Item } : new List<string>());
        foreach (var itemId in rawIds)
        {
            if (MongoId.IsValidMongoId(itemId)
                && itemDb.TryGetValue(new MongoId(itemId), out var template)
                && template.Properties?.QuestItem == true)
            {
                continue; // quest item - not shoppable
            }
            itemIds.Add(itemId);
        }
        if (itemIds.Count == 0)
        {
            return false; // category handover, or every option was a quest item
        }

        foreach (var itemId in itemIds)
        {
            string? name = Leaves.Utils.Instance.getLocale("en", itemId, " Name");
            itemNames.Add(name ?? itemId);
        }
        return true;
    }

    // Every HandoverItem turn-in across ALL of a profile's served quests, IGNORING quest status and
    // progress - locked, active, completed, failed alike. Unlike GetTurnInNeedsForProfile (the Info
    // page's live shopping list) this subtracts nothing and completes nothing: it's a debug "what does
    // this profile's whole quest tree ask you to hand over" view, so Remaining carries the FULL required
    // count of each turn-in. Quest items are still excluded (there's nothing to shop for). Read-only.
    public List<TurnInNeed> GetAllTurnInsForProfile(MongoId profileId)
    {
        var result = new List<TurnInNeed>();
        var (served, enLocale) = GetInMemoryProfileData(profileId);
        var itemDb = databaseService.GetItems();

        foreach (var (questId, quest) in served)
        {
            string questName = enLocale.TryGetValue($"{questId} name", out var rolledName)
                ? rolledName
                : (Leaves.Utils.Instance.getLocale("en", questId.ToString(), " name") ?? questId.ToString());
            questName = Leaves.Utils.Instance.RTT_Strip(questName);

            foreach (var condition in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
            {
                if (condition.ConditionType != "HandoverItem")
                {
                    continue;
                }
                if (!TryGetShoppableHandover(condition, itemDb, out var itemIds, out var itemNames))
                {
                    continue; // category handover, or every accepted item is a quest item
                }

                int required = condition.Value.HasValue && condition.Value.Value > 0
                    ? (int)Math.Ceiling(condition.Value.Value)
                    : 1;
                result.Add(new TurnInNeed(questName, itemIds, itemNames, condition.OnlyFoundInRaid == true, required));
            }
        }
        return result;
    }
    // Every quest the "re-roll all locked quests" web-UI action should refresh: quests THIS MOD rolled
    // (its per-profile cache) that the player has NOT accepted, completed, or failed. Its purpose is to
    // re-apply the CURRENT config to quests not yet in progress - e.g. after a weapon-category change -
    // without touching anything mid-flight. It is the complement of GetRerollableActiveQuests (which
    // lists ACCEPTED quests): this is the locked / not-yet-started set.
    //
    // Deliberately has NO admin override, unlike the targeted per-quest re-roll. The blacklist is
    // ALWAYS respected here. An admin option would be inconsistent anyway - the cache is the universe,
    // and only the subset of blacklisted quests that happen to be whitelisted-and-cached would be
    // reachable, so it could force-roll some protected quests but not others. A bulk maintenance
    // button that silently touches config-protected quests is a footgun, so it simply never does.
    public List<MongoId> GetLockedRerollableQuestIds(PmcData pmc)
    {
        var result = new List<MongoId>();
        if (pmc.Id is not { } profileId)
        {
            return result;
        }

        var (_, _, outlines) = GetWebUiProfileData(profileId);
        if (outlines.Count == 0)
        {
            return result; // nothing rolled yet - a first generation hasn't happened for this profile
        }

        var proceduralIds = generatedIds.ValuesSnapshot();
        var blacklist = new HashSet<MongoId>(Settings.Config.QuestBlacklist);

        // Quest ids the player has engaged with - protected from the bulk refresh. Quests absent from
        // pmc.Quests are implicitly still locked, so they never land here and stay refreshable.
        var engaged = new HashSet<MongoId>();
        if (pmc.Quests != null)
        {
            foreach (var questStatus in pmc.Quests)
            {
                if (IsEngagedStatus(questStatus.Status))
                {
                    engaged.Add(questStatus.QId);
                }
            }
        }

        foreach (var questId in outlines.Keys)
        {
            if (proceduralIds.Contains(questId))
            {
                continue; // the mod's own generated quests are never re-rolled through this path
            }
            if (engaged.Contains(questId))
            {
                continue; // accepted / completed / failed - leave it exactly as it is
            }
            if (blacklist.Contains(questId))
            {
                continue; // config-protected - never re-rolled by this bulk action
            }
            result.Add(questId);
        }
        return result;
    }

    // One accepted quest plus its objective lines, as the player actually reads them, for the web
    // UI's search tab.
    public sealed record ActiveQuestDetail(MongoId QuestId, string Name, string? Image, string Trader,
        string? TraderAvatar,
        List<Questrandomizer.ObjectiveDetail> Objectives, List<QuestLink> Previous, List<QuestLink> LeadsTo,
        List<Questrandomizer.RewardDetail> Rewards);

    // A neighbour in the quest chain: the id (for the client to key on) and the resolved display name.
    public sealed record QuestLink(string Id, string Name);

    // One quest for the debug BROWSE page: everything ActiveQuestDetail carries, plus its place in the
    // quest chain - Previous (the quests that must be done first) and LeadsTo (the quests it unlocks).
    public sealed record BrowseQuestDetail(MongoId QuestId, string Name, string? Image, string Trader,
        string? TraderAvatar,
        List<Questrandomizer.ObjectiveDetail> Objectives, List<QuestLink> Previous, List<QuestLink> LeadsTo,
        List<Questrandomizer.RewardDetail> Rewards);

    // True if this profile should be hidden from the web UI's profile dropdown - utility/alt accounts
    // (headless clients, dev profiles) matched by the configured name prefixes. The filter config now
    // lives ONCE in LeavesUtils (config.jsonc: WebUI_HiddenProfilePrefixes), shared across every mod's
    // web UI, so this just delegates to it. See GET /api/profiles / /turnins in QuestrandomizerRerollApi.
    public bool ShouldHideProfileFromWebUi(string nickname)
    {
        return Leaves.Utils.Instance.ShouldHideProfileFromWebUi(nickname);
    }

    // Every currently-accepted quest WITH its resolved objective text. Unlike GetRerollableActiveQuests
    // this never filters (no cache/blacklist/procedural gating) - it's a read-only reference view, so
    // it shows quests we rolled, quests we didn't, and procedural ones alike.
    //
    // Text resolution mirrors what the journal shows: the profile's OWN rolled locale first, falling
    // back to the shared/vanilla locale for objectives this mod never rewrote (many condition types
    // aren't randomized, so their text still lives under the vanilla condition id).
    public List<ActiveQuestDetail> GetActiveQuestDetails(PmcData pmc)
    {
        var result = new List<ActiveQuestDetail>();
        if (pmc.Id is not { } profileId || pmc.Quests == null)
        {
            return result;
        }

        var (served, enLocale, _) = GetWebUiProfileData(profileId);
        var realQuests = databaseService.GetQuests();
        var conditionCounters = BuildConditionCounters(pmc);
        var (prereqs, leadsTo) = BuildQuestChains(served);
        var chainLocale = enLocale ?? new Dictionary<string, string>();

        foreach (var questStatus in pmc.Quests)
        {
            if (questStatus.Status != QuestStatusEnum.Started && questStatus.Status != QuestStatusEnum.AvailableForFinish)
            {
                continue; // not accepted, or already completed/failed/locked
            }

            // The SERVED copy is what the player sees; fall back to the vanilla definition only if the
            // profile somehow isn't in the context.
            if (!served.TryGetValue(questStatus.QId, out var quest)
                && !realQuests.TryGetValue(questStatus.QId, out quest))
            {
                continue;
            }

            string name = enLocale != null && enLocale.TryGetValue($"{questStatus.QId} name", out var rolledName)
                ? rolledName
                : (Leaves.Utils.Instance.getLocale("en", questStatus.QId.ToString(), " name") ?? questStatus.QId.ToString());

            result.Add(new ActiveQuestDetail(
                questStatus.QId, Leaves.Utils.Instance.RTT_Strip(name), quest.Image,
                Leaves.Utils.Instance.getTraderNickname(quest.TraderId),
                Leaves.Utils.Instance.getTraderAvatar(quest.TraderId),
                ResolveObjectives(quest, enLocale, questStatus.CompletedConditions, conditionCounters),
                BuildQuestLinks(prereqs.TryGetValue(questStatus.QId, out var p) ? p : new List<MongoId>(), chainLocale),
                BuildQuestLinks(leadsTo.TryGetValue(questStatus.QId, out var l) ? l : new List<MongoId>(), chainLocale),
                ResolveRewards(quest)));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // EVERY quest in the profile's served table with resolved objective text, for the debug browse
    // page. Unlike GetActiveQuestDetails this ignores quest status entirely (the whole tree, accepted
    // or not, rolled / vanilla / procedural alike) and resolves NO completion state - it's a pure
    // "what does this quest look like for this profile" view, so it needs only a profile id, not a
    // PmcData with its quest-status list.
    public List<BrowseQuestDetail> GetAllProfileQuestDetails(MongoId profileId)
    {
        var (served, enLocale) = GetInMemoryProfileData(profileId);
        var (prereqs, leadsTo) = BuildQuestChains(served);

        var result = new List<BrowseQuestDetail>();
        foreach (var (questId, quest) in served)
        {
            string name = enLocale.TryGetValue($"{questId} name", out var rolledName)
                ? rolledName
                : (Leaves.Utils.Instance.getLocale("en", questId.ToString(), " name") ?? questId.ToString());

            // No completion args -> every objective resolves as not-done with no counter; the debug
            // page renders the text only, so that state is never shown anyway.
            result.Add(new BrowseQuestDetail(
                questId, Leaves.Utils.Instance.RTT_Strip(name), quest.Image,
                Leaves.Utils.Instance.getTraderNickname(quest.TraderId),
                Leaves.Utils.Instance.getTraderAvatar(quest.TraderId),
                ResolveObjectives(quest, enLocale),
                BuildQuestLinks(prereqs.TryGetValue(questId, out var p) ? p : new List<MongoId>(), enLocale),
                BuildQuestLinks(leadsTo.TryGetValue(questId, out var l) ? l : new List<MongoId>(), enLocale),
                ResolveRewards(quest)));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    // The quest-chain relation over a served table, built in one pass so every consumer shares it.
    // PREREQUISITES of a quest are the "Quest" conditions in its AvailableForStart (each Target.Item is
    // a required quest id); LEADS-TO is that relation reversed - every quest that names this one as a
    // prerequisite. Both are keyed by quest id; a quest absent from a map simply has no such neighbours.
    private (Dictionary<MongoId, List<MongoId>> Prereqs, Dictionary<MongoId, List<MongoId>> LeadsTo)
        BuildQuestChains(Dictionary<MongoId, Quest> served)
    {
        var prereqs = new Dictionary<MongoId, List<MongoId>>();
        var leadsTo = new Dictionary<MongoId, List<MongoId>>();
        foreach (var (questId, quest) in served)
        {
            var mine = new List<MongoId>();
            foreach (var condition in quest.Conditions?.AvailableForStart ?? new List<QuestCondition>())
            {
                if (condition.ConditionType != "Quest")
                {
                    continue;
                }
                string? targetStr = condition.Target?.Item;
                if (targetStr == null || !MongoId.IsValidMongoId(targetStr))
                {
                    continue;
                }
                MongoId target = targetStr;
                if (mine.Contains(target))
                {
                    continue; // a quest can repeat the same prereq under several status conditions
                }
                mine.Add(target);

                if (!leadsTo.TryGetValue(target, out var unlockers))
                {
                    unlockers = new List<MongoId>();
                    leadsTo[target] = unlockers;
                }
                if (!unlockers.Contains(questId))
                {
                    unlockers.Add(questId);
                }
            }
            prereqs[questId] = mine;
        }
        return (prereqs, leadsTo);
    }

    // Resolves a set of quest ids into (id, name) links for the browse page's chain panel, name-sorted.
    // Names come from the profile's OWN rolled locale first (what the journal shows), then the shared
    // locale, then the raw id for anything unnameable.
    private List<QuestLink> BuildQuestLinks(List<MongoId> ids, Dictionary<string, string> enLocale)
    {
        var links = new List<QuestLink>();
        foreach (var id in ids)
        {
            string name = enLocale.TryGetValue($"{id} name", out var rolled)
                ? rolled
                : (Leaves.Utils.Instance.getLocale("en", id.ToString(), " name") ?? id.ToString());
            links.Add(new QuestLink(id.ToString(), Leaves.Utils.Instance.RTT_Strip(name)));
        }
        links.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return links;
    }

    // Snapshot the player's profile AND this mod's generated data for them, BEFORE a web-UI action
    // that rewrites either (re-roll / re-play). Both stores must move together - the rolled quest
    // definitions live here while the accepted-quest progress lives in the profile - so a restore has
    // to put back both or they desync.
    //
    // Entries are stored at paths relative to the SPT ROOT (user/profiles/<id>.json and
    // user/mods/Leaves-Questrandomizer/assets/generated/<id>/...), so the zip can be extracted
    // straight over the game folder to restore. Returns the zip path, or null if there was nothing to
    // snapshot. Deliberately never throws: a failed backup logs loudly but must not break the action.
    public string? CreateWebUiBackup(MongoId profileId, string reason, string? nickname = null)
    {
        try
        {
            // modPath = <root>/user/mods/Leaves-Questrandomizer -> walk up three for the game root.
            string? root = System.IO.Directory.GetParent(modPath)?.Parent?.Parent?.FullName;
            if (root == null)
            {
                return null;
            }

            var files = new List<string>();
            string profileFile = System.IO.Path.Combine(root, "user", "profiles", $"{profileId}.json");
            if (System.IO.File.Exists(profileFile))
            {
                files.Add(profileFile);
            }
            string generatedDir = System.IO.Path.Combine(modPath, "assets", "generated", profileId.ToString());
            if (System.IO.Directory.Exists(generatedDir))
            {
                files.AddRange(System.IO.Directory.GetFiles(generatedDir, "*", System.IO.SearchOption.AllDirectories));
            }
            if (files.Count == 0)
            {
                return null; // nothing rolled yet and no profile on disk - nothing worth zipping
            }

            string backupDir = System.IO.Path.Combine(modPath, "backups");
            System.IO.Directory.CreateDirectory(backupDir);
            // Lead with the account name so backups are identifiable at a glance; the id stays for
            // uniqueness (nicknames aren't guaranteed unique and can contain anything).
            string namePart = string.IsNullOrWhiteSpace(nickname) ? "" : SanitizeForFileName(nickname!) + "_";
            string zipPath = System.IO.Path.Combine(
                backupDir, $"{namePart}{profileId}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{reason}.zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string file in files)
                {
                    // Root-relative, forward slashes - keeps the in-zip tree identical to the install.
                    string entry = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
                }
            }

            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Backup before {reason}: {files.Count} file(s) -> {zipPath}", LogTextColor.Cyan);
            return zipPath;
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Backup before {reason} FAILED (continuing anyway): {ex.Message}", LogTextColor.Red);
            return null;
        }
    }

    // ===== Web-UI action history =====
    // One shared, server-side log of every re-roll / re-play made through the web UI, so the whole
    // coop group can see who changed what and when ("Leaves re-rolled QuestA, QuestB"). Persisted to
    // assets/generated/webui_history.json; capped so it can't grow forever.
    // Ip is nullable and LAST so entries written before it existed still deserialize (missing JSON
    // property -> null). It records WHICH MACHINE performed the action - the page has no auth, and
    // Nickname is only the profile that was changed, not necessarily who clicked.
    public sealed record WebUiHistoryEntry(long Time, string Nickname, string Action, List<string> Quests, bool Admin, string? Ip = null);

    private const string HistoryFile = "assets/generated/webui_history.json";
    private const int HistoryMaxEntries = 1000;
    private readonly object historyGate = new();
    private List<WebUiHistoryEntry>? historyCache;

    public void AppendWebUiHistory(string nickname, string action, List<string> questNames, bool admin, string? ip = null)
    {
        try
        {
            lock (historyGate)
            {
                var list = LoadHistory();
                list.Add(new WebUiHistoryEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), nickname, action, questNames, admin, ip));
                if (list.Count > HistoryMaxEntries)
                {
                    list.RemoveRange(0, list.Count - HistoryMaxEntries);
                }
                Leaves.Utils.Instance.saveJsonFile(list, modPath, HistoryFile);
            }
        }
        catch (Exception ex)
        {
            // History is a nicety - never let it break the action that just succeeded.
            Leaves.Utils.Instance.printColor($"[Questrandomizer] Failed to write web-UI history: {ex.Message}", LogTextColor.Red);
        }
    }

    public List<WebUiHistoryEntry> GetWebUiHistory()
    {
        lock (historyGate)
        {
            return new List<WebUiHistoryEntry>(LoadHistory());
        }
    }

    private List<WebUiHistoryEntry> LoadHistory()
    {
        historyCache ??= System.IO.File.Exists(System.IO.Path.Combine(modPath, HistoryFile))
            ? modHelper.GetJsonDataFromFile<List<WebUiHistoryEntry>>(modPath, HistoryFile)
            : new List<WebUiHistoryEntry>();
        return historyCache;
    }

    // The quest names a player would recognize RIGHT NOW (their rolled locale, vanilla fallback).
    // Callers logging a re-roll must resolve names BEFORE the re-roll - the roll rewrites them.
    public List<string> ResolveQuestNamesForWebUi(MongoId profileId, IEnumerable<MongoId> questIds)
    {
        var (_, enLocale, _) = GetWebUiProfileData(profileId);
        var names = new List<string>();
        foreach (var questId in questIds)
        {
            string name = enLocale != null && enLocale.TryGetValue($"{questId} name", out var rolled)
                ? rolled
                : (Leaves.Utils.Instance.getLocale("en", questId.ToString(), " name") ?? questId.ToString());
            names.Add(Leaves.Utils.Instance.RTT_Strip(name));
        }
        return names;
    }

    // Re-run the one-session desync repair for one profile ON DEMAND (the web UI's "Verify quests"
    // button), so a quest stuck with every objective ticked but no Complete button can be healed WITHOUT
    // a server restart. Ensures the profile is generated, then runs the same OneSessionQuestRepair the
    // boot path runs, under the per-profile gate so it can't race a generation or a re-roll (the gate is
    // a reentrant monitor, so EnsureProfileDataGenerated re-taking it inside is fine). Returns the display
    // names of the quests it healed - empty when nothing was wrong. The heal writes the profile's live
    // counters; the player still has to REOPEN the trader / quest screen for the client to re-read them
    // (no client restart needed).
    public List<string> VerifyAndRepairProfile(MongoId profileId)
    {
        var gate = generationGates.GetOrAdd(profileId, _ => new object());
        lock (gate)
        {
            EnsureProfileDataGenerated(profileId);
            if (!QuestrandomizerContext.TryGetQuestsForProfile(profileId, out var served) || served == null)
            {
                return new List<string>();
            }
            var healedIds = oneSessionQuestRepair.Repair(profileId, served);
            return ResolveQuestNamesForWebUi(profileId, healedIds);
        }
    }

    // Nicknames are free text (spaces, punctuation, emoji), so strip anything the filesystem rejects
    // before putting one in a file name.
    private static string SanitizeForFileName(string value)
    {
        var cleaned = new string(value.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        return cleaned.Trim().Length == 0 ? "profile" : cleaned.Trim();
    }

    // A quest's objective lines as the journal shows them: the profile's OWN rolled locale first,
    // falling back to the shared/vanilla locale for objectives this mod never rewrote (many condition
    // types aren't randomized, so their text still lives under the vanilla condition id). Shared by
    // the re-roll, re-play and search lists so all three read identically.
    // One objective line plus how far along it is. Completed/Current/Target are all "unknown" for
    // callers that don't pass progress (re-play lists completed quests, where it means nothing) - the
    // web UI then just renders the text, so an absent progress source is never shown as "0 done".
    public sealed record ObjectiveDetail(string Text, bool Completed, double? Current, double? Target);

    // One line in a quest's reward list, resolved to display-ready English. Kind drives how the client
    // formats it: "item" (Name = item name, Count = the combined stack across every reward entry for that
    // template), "xp" (Value = experience points), "rep" (Name = trader, Value = standing delta),
    // "skill" (Name = skill id, Value = points), "traderUnlock" (Name = trader). Only SUCCESS rewards are
    // carried - the payout for finishing the quest, never fail penalties.
    public sealed record RewardDetail(string Kind, string Name, int Count, double Value);

    // Builds a profile's per-condition progress lookup once: condition id -> current count. The
    // counters are keyed by their own id, but each carries the CONDITION id it tracks in .Id (and the
    // owning quest in .SourceId), which is what the objectives below match on.
    internal static Dictionary<string, double> BuildConditionCounters(PmcData pmc)
    {
        var counters = new Dictionary<string, double>();
        foreach (var counter in pmc.TaskConditionCounters?.Values ?? Enumerable.Empty<TaskConditionCounter>())
        {
            if (counter.Id is { } conditionId && counter.Value is { } value)
            {
                counters[conditionId.ToString()] = value;
            }
        }
        return counters;
    }

    internal List<ObjectiveDetail> ResolveObjectives(
        Quest quest,
        Dictionary<string, string>? enLocale,
        IReadOnlyCollection<string>? completedConditions = null,
        IReadOnlyDictionary<string, double>? conditionCounters = null)
    {
        var objectives = new List<ObjectiveDetail>();
        foreach (var condition in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
        {
            string conditionId = condition.Id.ToString();
            string? text = enLocale != null && enLocale.TryGetValue(conditionId, out var rolledText)
                ? rolledText
                : Leaves.Utils.Instance.getLocale("en", conditionId);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Completion is decided from whichever source the GAME itself trusts for this objective:
            //
            //  - Counter-style objective (has a live TaskConditionCounter): the counter is authoritative.
            //    CompletedConditions goes STALE here - an objective's id is added when its counter first
            //    reaches target, but if the counter later resets (e.g. "Locate the secret exfil", whose
            //    count drops back to 0 between raids) the id is left orphaned in CompletedConditions. The
            //    client renders these from the counter, so trusting CompletedConditions showed a ✔ the
            //    game did not. Done only once the counter meets its target.
            //  - No counter (e.g. a find-in-raid item): there's nothing live to read, so CompletedConditions
            //    is the only signal, and it's reliable for that binary case.
            double? current = null;
            bool completed;
            if (conditionCounters != null && conditionCounters.TryGetValue(conditionId, out var counted))
            {
                current = counted;
                // Guard the target: a counter condition should need at least 1, never "0 always done".
                double target = condition.Value.HasValue && condition.Value.Value > 0 ? condition.Value.Value : 1;
                completed = counted >= target;
            }
            else
            {
                completed = completedConditions?.Contains(conditionId) == true;
            }
            // Only a counter-style condition has a meaningful target; for the rest it stays null and
            // the UI shows a plain done/not-done state instead of "x / y".
            objectives.Add(new ObjectiveDetail(Leaves.Utils.Instance.RTT_Strip(text), completed, current, condition.Value));
        }
        return objectives;
    }

    // The SUCCESS payout for a quest, flattened to the handful of reward kinds worth showing on a card
    // and resolved to English display text. Item rewards are COMBINED by template: quest data lists ten
    // grenades as ten separate one-count reward entries (rather than one stack of ten), so we sum every
    // entry's count per item and emit one "F-1 Hand Grenade x10" line instead of ten identical lines.
    // Reward kinds we don't surface (assort unlocks, stash rows, customization, ...) are skipped rather
    // than shown as noise. Runs against the SERVED quest, so a re-rolled quest reports its rolled rewards.
    internal List<RewardDetail> ResolveRewards(Quest quest)
    {
        var rewards = new List<RewardDetail>();
        var successRewards = quest.Rewards?.GetValueOrDefault("Success");
        if (successRewards == null)
        {
            return rewards;
        }

        // Item rewards are aggregated by template id; itemOrder preserves first-seen order so the list
        // reads the same every time instead of in hash order.
        var itemCounts = new Dictionary<string, int>();
        var itemOrder = new List<string>();

        foreach (var reward in successRewards)
        {
            switch (reward.Type)
            {
                case RewardType.Experience:
                    if (reward.Value is { } xp && xp != 0)
                    {
                        rewards.Add(new RewardDetail("xp", "", 0, xp));
                    }
                    break;

                case RewardType.Item:
                    if (reward.Items != null)
                    {
                        foreach (var item in reward.Items)
                        {
                            // A reward's Items list holds the root reward item PLUS any attached mods; only
                            // the root (its id matches the reward's Target group id) is the reward itself,
                            // so attachments aren't counted as separate rewards. A reward with no Target
                            // (nothing to disambiguate against) takes every item.
                            if (reward.Target is { Length: > 0 } target && item.Id.ToString() != target)
                            {
                                continue;
                            }
                            string templateId = item.Template.ToString();
                            int count = (int)(item.Upd?.StackObjectsCount ?? 1);
                            if (!itemCounts.ContainsKey(templateId))
                            {
                                itemOrder.Add(templateId);
                            }
                            itemCounts[templateId] = itemCounts.GetValueOrDefault(templateId) + count;
                        }
                    }
                    break;

                case RewardType.TraderStanding:
                    if (reward.Value is { } rep && rep != 0)
                    {
                        rewards.Add(new RewardDetail("rep", TraderRewardName(reward.Target), 0, rep));
                    }
                    break;

                case RewardType.Skill:
                    if (reward.Value is { } points && points != 0)
                    {
                        rewards.Add(new RewardDetail("skill", reward.Target ?? "", 0, points));
                    }
                    break;

                case RewardType.TraderUnlock:
                    rewards.Add(new RewardDetail("traderUnlock", TraderRewardName(reward.Target), 0, 0));
                    break;
            }
        }

        // The combined item lines come after the flat rewards above, in first-seen order. Reward items
        // use their FULL name (ShortName read as too cryptic here); the narrow rewards column truncates
        // over-long names in the client with an ellipsis, keeping the full name in a hover tooltip.
        foreach (var templateId in itemOrder)
        {
            string name = Leaves.Utils.Instance.getLocale("en", templateId, " Name") ?? templateId;
            rewards.Add(new RewardDetail("item", Leaves.Utils.Instance.RTT_Strip(name), itemCounts[templateId], 0));
        }

        return rewards;
    }

    // A trader-standing / trader-unlock reward names its trader by id in Target; resolve it to the
    // trader's DISPLAY nickname, or empty when there's nothing to resolve. The locale nickname is what
    // the game shows (e.g. the arena trader's Base.Nickname is "Arena" but the EN locale calls him
    // "Ref"), so prefer the "<id> Nickname" locale key and only fall back to Base.Nickname.
    private static string TraderRewardName(string? traderId)
    {
        if (traderId is not { Length: > 0 } id)
        {
            return "";
        }
        string? localeName = Leaves.Utils.Instance.getLocale("en", id, " Nickname");
        if (!string.IsNullOrWhiteSpace(localeName))
        {
            return localeName;
        }
        return Leaves.Utils.Instance.getTraderNickname(new MongoId(id));
    }

    // Re-roll the randomization of specific quests for one profile: drop their stored outlines so
    // GenerateProfileData rolls them fresh (RollQuest uses Random.Shared, so a re-roll is a genuinely
    // new roll), rebuilding every other quest from its untouched outline. Takes the SAME
    // per-profile gate as normal generation, so it can't race a first-time generation or a second
    // concurrent re-roll. Returns the ids actually re-rolled (requested ids that were ours to roll
    // and not procedural). The CALLER resets each returned quest's PROGRESS in the player profile -
    // this only touches the rolled DEFINITION (see REROLL_SPEC.md §1: both stores must change).
    // admin: user opted into the re-roll web UI's admin override, which lets blacklisted quests (and
    // quests never rolled before, e.g. a whitelist-excluded vanilla quest) be force-rolled anyway.
    // At the user's own risk - it edits quests the config deliberately protected.
    public List<MongoId> RerollProfileQuests(MongoId profileId, IReadOnlyCollection<MongoId> questIds, bool admin = false)
    {
        var gate = generationGates.GetOrAdd(profileId, _ => new object());
        lock (gate)
        {
            // The OUTLINE is the source of truth, so a re-roll drops the stored decisions and lets
            // GenerateProfileData roll the quest fresh (an outline left in place would just rebuild the
            // identical quest - a re-roll that re-rolled nothing).
            var cachedOutlines = LoadCachedOutlines(profileId);
            var proceduralIds = generatedIds.ValuesSnapshot();
            var blacklist = new HashSet<MongoId>(Settings.Config.QuestBlacklist);

            var rerolled = new List<MongoId>();
            foreach (var questId in questIds)
            {
                if (proceduralIds.Contains(questId))
                {
                    continue; // procedural quests are the mod's own - never re-rolled through here
                }
                if (!admin && blacklist.Contains(questId))
                {
                    continue; // blacklisted quests are protected unless the admin override is on
                }
                // Drop the outline so it re-rolls fresh. In admin mode a quest that was never rolled has
                // no outline (Remove is a no-op), but we still force-roll it for the first time.
                bool hadOutline = cachedOutlines.Remove(questId);
                if (hadOutline || admin)
                {
                    rerolled.Add(questId);
                }
            }

            if (rerolled.Count == 0)
            {
                return rerolled;
            }

            // Persist the trimmed outlines so the re-rolled quests genuinely have no decisions to rebuild
            // from and GenerateProfileData rolls them fresh.
            string trimmedOutlines = jsonUtil.Serialize(cachedOutlines, true) ?? "{}";
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(modPath, $"assets/generated/{profileId}/outline.jsonc"),
                annotator.Annotate(trimmedOutlines));

            //Remove from context so it gets regenerated.
            QuestrandomizerContext.RemoveProfile(profileId);
            // In admin mode the re-rolled ids may be blacklisted/whitelist-excluded, so pass them as
            // force-ids to bypass those gates during regeneration; normal re-rolls don't need it.
            GenerateProfileData(profileId, admin ? new HashSet<MongoId>(rerolled) : null);

            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Re-rolled {rerolled.Count} quest(s) for profile {profileId}.",
                LogTextColor.Cyan);
            return rerolled;
        }
    }
}
