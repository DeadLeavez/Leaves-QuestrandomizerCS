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

// See QuestrandomizerContext.cs for the full explanation of the per-profile swapping mechanism
// this all sits on top of.
//
// Startup is split in two: OnLoad here (PostDBModLoader stage) does everything that only needs
// the vanilla database, while QuestrandomizerLateSetup calls LateOnLoad below at the
// PostSptModLoader stage - after EVERY mod's OnLoad has finished - for the parts that need to
// see other mods' items (dynamic weapon selectors, and therefore handbook generation).
//
// Singleton is REQUIRED here, not optional: QuestrandomizerLateSetup injects this class as a
// plain constructor parameter while the IOnLoad collection resolves it separately - without
// Singleton those would be two different instances, and LateOnLoad would run against one whose
// OnLoad never did (the same dual-resolution pitfall documented on LeavesUtils).
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public partial class Questrandomizer(
    DatabaseService databaseService,
    ModHelper modHelper,
    HandbookHelper handbookHelper,
    ProfileHelper profileHelper,
    Leaves.TierList tierList,
    ICloner cloner,
    JsonUtil jsonUtil,
    Leaves.JsonIdAnnotator annotator,
    CustomItemService customItemService,
    ConfigServer configServer,
    PatchManager patchManager,
    // Every route handler in the whole server is backed by one of these. Injecting the full list
    // lets us subscribe to ALL of them at once, the same way SPT's own HttpRouter class does.
    IEnumerable<StaticRouter> staticRouters,
    IEnumerable<DynamicRouter> dynamicRouters,
    QuestrandomizerCompatibility compatibility,
    // Heals the one-session desync (green ✔ objectives but a greyed-out Complete button) on any wounded
    // quest, ours or vanilla - see OneSessionQuestRepair. Runs on every profile load below, and again on
    // demand from the web UI's "Verify quests" button.
    OneSessionQuestRepair oneSessionQuestRepair)
    : IOnLoad
{
    // One lock object per profile so concurrent requests for the SAME fresh profile generate
    // once instead of racing (see EnsureProfileDataGenerated). The cross-profile generatedIds
    // store + its file are now guarded by IdStore's own internal lock (see Leaves.IdStore).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<MongoId, object> generationGates = new();

    private QuestRoller questRoller = null!;
    // Repairs stored outlines into shapes the rest of the pipeline handles, before regeneration reads
    // them (see OutlineVerification). Built in OnLoad alongside the roller it re-rolls through.
    private OutlineVerification outlineVerification = null!;
    private QuestGeneration questGeneration = null!;
    private Leaves.IdStore generatedIds = null!;
    private string modPath = null!;
    // Built in LateOnLoad (it needs the fully resolved categories). Stage 3 of the outline pipeline -
    // runs on every profile generation to rebuild quests from their stored outlines.
    private QuestRegeneration? regeneration;
    // The ids of the VANILLA quests, read once in LateOnLoad from the pristine on-disk database (mods
    // mutate the in-memory DB, never the file), so a quest in the live DB but absent here is modded.
    // Used to prefix modded quests' names with "(M)". Empty if the file couldn't be read.
    private HashSet<MongoId> vanillaQuestIds = new HashSet<MongoId>();
    // weapon -> every weapon that TRANSITIVELY counts as it (config equivalents + runtime ones like
    // ShinyAirdropGuns' clones), flattened once in LateOnLoad so equivalence chains (shiny of a modded
    // gun -> modded gun -> vanilla gun) resolve at any depth. Shared by the quest-condition injection
    // (LoadWeaponEquivalents), the in-game handbook sheet, and the web UI - see
    // QuestrandomizerCompatibility.BuildTransitiveEquivalents.
    private Dictionary<MongoId, List<MongoId>> transitiveWeaponEquivalents = new();

    public Task OnLoad()
    {
        Leaves.Utils.Instance.printColor("[Questrandomizer] Starting up!", LogTextColor.Cyan);

        modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        Settings.Init(modPath, modHelper, databaseService, tierList);
        Leaves.Utils.Instance.printColor(
            // Raw count, not ResolvedCategories - those aren't compiled until LateOnLoad now.
            $"[Questrandomizer] Loaded settings: {Settings.WeaponCategories.Categories.Count} weapon categories, "
            + $"{Settings.HandoverCategories.Count} handover categories, {Settings.GearCategories.Count} gear groups, "
            + $"{Settings.QuestWhitelist.Count} whitelisted quests, {Settings.ItemTierList.Count} item tiers, "
            + $"{Settings.WeaponEquivalents.Count} weapon equivalents.",
            LogTextColor.Cyan);

        var itemDB = databaseService.GetItems();
        // Static helpers, initialised in dependency order: locale table, then the quest-depth graph,
        // then the fact layer (which reads the depth graph). All read the already-static Settings and
        // Leaves.Utils.Instance (self-registered when DI built LeavesUtils, before this OnLoad), so they
        // must come after Settings.Init above.
        QuestLocaleGeneration.Init(modPath);
        QuestTools.Init(modPath, itemDB, databaseService.GetQuests());
        QuestFacts.Init(itemDB, modPath, annotator);
        // The outline-native roll (stage 2). Reads its facts via the static QuestFacts; used by the
        // legacy-cache converter (to roll a fresh weapon requirement) and, later, by the pipeline self-test.
        questRoller = new QuestRoller(tierList, handbookHelper);
        outlineVerification = new OutlineVerification(questRoller);

        // Generated-quest ids are shared across every profile (not per-profile like the quests
        // themselves) - the same quest "slot" (e.g. "QG_WeaponMastery0") needs the same id no
        // matter which profile generates it, so different profiles' clients don't get confused
        // about what a shared/re-used id refers to. This file isn't shipped by the build (see the
        // .csproj for why - it's runtime-owned), so a fresh install won't have it yet.
        generatedIds = new Leaves.IdStore(modPath, "assets/generated/ids.jsonc");
        questGeneration = new QuestGeneration(tierList, generatedIds);

        // Hand our patches to SPT's own patch manager and switch them on. "PatcherName" is just an
        // identifying label Harmony uses internally (shows up if you ever need to debug which mod
        // patched what) - it doesn't need to match anything else.
        patchManager.PatcherName = "Leaves.Questrandomizer";
        patchManager.AddPatch(new GetQuestsPatch());
        patchManager.AddPatch(new GetLocaleDbPatch());
        patchManager.EnablePatches();

        // Subscribe to every router's "about to handle a request" event. This is what lets us
        // find out, for every single incoming request, which profile it belongs to - BEFORE the
        // real handler (and therefore any DatabaseService.GetQuests() call our patch is watching)
        // runs. See QuestrandomizerContext.cs for why this is safe under multiple simultaneous
        // requests (Fika coop) where the old TS version wasn't.
        foreach (var router in staticRouters)
        {
            router.OnBeforeAction += OnRouterBeforeAction;
        }
        foreach (var router in dynamicRouters)
        {
            router.OnBeforeAction += OnRouterBeforeAction;
        }

        Leaves.Utils.Instance.printColor("[Questrandomizer] Now watching every request for profile context switches.", LogTextColor.Cyan);

        return Task.CompletedTask;
    }

    // Runs at the PostSptModLoader stage (called by QuestrandomizerLateSetup), i.e. strictly
    // after EVERY mod's OnLoad has completed - so weapons other mods added to the item database
    // exist by now, and equivalents other mods registered are all in. Everything here depends on
    // final weapon-category membership:
    //   1. Selector resolution fills categories with matching live-database weapons.
    //   2. Handbook categories/sheets/descriptions are generated from that final membership.
    //   3. The compat handler wiring replays every registered equivalent against it.
    public void LateOnLoad()
    {
        // Order is load-bearing. Mod categories must be topped up BEFORE compilation, because
        // compilation bakes their ids into every category's required/forbidden groups; compilation
        // itself waits until here so it sees other mods' items at all.
        Settings.ApplyModCategoryAdditions();
        Settings.CompileAllCategories();

        Settings.ResolveDynamicWeapons(databaseService.GetItems());

        // Merge the compat files' category additions BEFORE finalizing, so mod weapons join their
        // categories and then go through the same pruning as everything else.
        Settings.ApplyCategoryAdditions();

        // Prune weapon ids whose mod isn't installed and drop categories left empty, now that every
        // mod's items are present.
        Settings.FinalizeCategories(databaseService.GetItems());

        // Flatten the equivalence graph once, now that BOTH sources are final (config equivalents are
        // parsed at settings construction; runtime ones - e.g. ShinyAirdropGuns' clones - were all
        // registered during other mods' OnLoad, which completed before this PostSptModLoader stage).
        // Every equivalence consumer below reads this instead of walking the raw one-hop pairs, so a
        // shiny clone of a modded gun rides the whole chain up to the real category/quest member.
        transitiveWeaponEquivalents = BuildTransitiveWeaponEquivalents();

        // Handbook categories are global, one-time content (every player sees the same "AK
        // category" browsing entry) - not part of the per-profile pipeline, so it runs straight
        // through to completion right here. See QuestHandbookGeneration.cs for why it writes
        // locale text differently (AddTransformer) than the per-profile quest text does
        // (LocaleWorkspace).
        var handbookGeneration = new QuestHandbookGeneration(modPath, databaseService, customItemService, configServer, generatedIds, transitiveWeaponEquivalents);
        handbookGeneration.Generate();
        generatedIds.Save();

        // Wiring this AFTER Generate means RegisterEquivalent replays every equivalent (from
        // config AND other mods) against the final resolved categories - each shiny clone gets
        // its "Part of category"/"Equivalent to" description lines exactly once. Any equivalents
        // somehow registered even later still arrive live through the handler.
        compatibility.SetEquivalentAddedHandler(handbookGeneration.RegisterEquivalent);

        // Outline pipeline. Stage 3 needs the FINAL resolved categories, so it can only be built here.
        regeneration = new QuestRegeneration();

        // Read the pristine vanilla quest ids now (single-threaded) so per-profile generation can tell a
        // modded quest from a vanilla one without racing on a lazy load.
        vanillaQuestIds = LoadVanillaQuestIds();

        if (Settings.Config.RollerSelfTest)
        {
            Leaves.Utils.Instance.printColor("[Questrandomizer] Roller self-test ENABLED - rolling every quest through the new roller.", LogTextColor.Cyan);
            new QuestRollerSelfTest(questRoller, questGeneration, regeneration, cloner, jsonUtil)
                .Run(databaseService.GetQuests());
        }
    }

    // This method runs synchronously, once per incoming API request, right before that request's
    // real work happens. "object? sender" is just standard C# event-handler shape (every event
    // handler gets one) - we don't use it here.
    private void OnRouterBeforeAction(object? sender, IOnBeforeEventRequestData e)
    {
        // The event hands us a generic "something happened" object - StaticDynamicOnBeforeEventRequestData
        // is the specific shape both StaticRouter and DynamicRouter actually send, and it's the
        // one that carries the session id we need. If it's some other shape we don't recognise,
        // just ignore it.
        if (e is not StaticDynamicOnBeforeEventRequestData data)
        {
            return;
        }

        MongoId profileId = data.SessionId;
        if (profileId == MongoId.Empty())
        {
            // No session cookie on this request (e.g. a health-check or very first handshake) -
            // nothing to attach a profile's quests to.
            return;
        }

        EnsureProfileDataGenerated(profileId);

        // From this point until this request finishes, GetQuestsPatch.cs and GetLocaleDbPatch.cs
        // will see this profile id and can hand back that profile's own data instead of the
        // shared originals.
        QuestrandomizerContext.CurrentProfileId = profileId;
    }

    // Builds this profile's own randomized quest set (and the locale text describing it) the
    // first time we see a request for it. C# equivalent of the TS original's
    // LeavesQuestManager.load(), including the disk cache: a quest's randomized form, once
    // generated for a profile, is saved to assets/generated/<profileId>/ and reused forever after
    // (never re-rolled on a later server restart) - otherwise a returning player's in-progress
    // kill/handover counter could end up tracking against conditions that no longer match what the
    // server expects, silently breaking quest completion.
    private void EnsureProfileDataGenerated(MongoId profileId)
    {
        if (QuestrandomizerContext.TryGetQuestsForProfile(profileId, out _))
        {
            // Already built this profile's data on a previous request - nothing to do. This
            // lock-free fast path is what every request after the first takes.
            return;
        }

        // The game client fires requests in PARALLEL (the trader screen alone sends several at
        // the same millisecond), so multiple requests for the same not-yet-generated profile all
        // reach this point together. Without serialization they'd all run the full generation at
        // once - which corrupted the shared locale map and double-wrote quests.json in live
        // testing. One lock object per profile: the first request in generates while the others
        // wait at the lock, then see the data exists (the re-check below) and leave. Requests
        // for OTHER profiles use different lock objects and don't wait at all - important for
        // Fika coop, where a fresh profile's ~seconds-long generation must not stall everyone.
        var gate = generationGates.GetOrAdd(profileId, _ => new object());
        lock (gate)
        {
            if (QuestrandomizerContext.TryGetQuestsForProfile(profileId, out _))
            {
                return; // someone else generated it while we waited for the lock
            }
            GenerateProfileData(profileId);
        }
    }

    // forceRerollIds: admin-override quest ids (from the re-roll web UI) that must be edited even if
    // they'd normally be skipped by the trader/quest whitelist or the blacklist - the caller has
    // already removed them from the cache so they get a genuinely fresh roll. Null for normal runs.
    private void GenerateProfileData(MongoId profileId, IReadOnlySet<MongoId>? forceRerollIds = null)
    {
        // The OUTLINE is the sole source of truth. Read the real vanilla data now (before this
        // profile's context swap is armed below) so regeneration always lays decisions over today's
        // vanilla quests, membership and wording.
        var realQuests = databaseService.GetQuests();
        var profileQuests = cloner.Clone(realQuests)!;
        var workspace = new LocaleWorkspace();

        var loadedOutlines = LoadCachedOutlines(profileId);
        if (loadedOutlines.Count == 0)
        {
            // ONE-TIME MIGRATION of a legacy profile that still has an old quests.json full-quest cache
            // but no outlines: convert its ENGAGED quests to outlines (weapon/mod stripped, but their
            // condition ids KEPT - see QuestOutlineConverter - so a live player's in-progress quests are
            // not orphaned). Locked quests are left without an outline and fall through to a fresh roll
            // below. The consumed quests.json/locales.json are deleted at the end of this method; from
            // here on the profile is outline-only and this block never runs for it again.
            if (TryLoadLegacyQuestCache(profileId, out var legacyQuests))
            {
                loadedOutlines = MigrateLegacyCache(profileId, legacyQuests, realQuests);
            }
        }

        // Outlines for quests rolled THIS run, merged over whatever was already stored (or just migrated).
        var outlines = new Dictionary<MongoId, QuestOutline>(loadedOutlines);

        // Repair any stored outline that carries a shape the rest of the pipeline mishandles, BEFORE
        // regeneration reads it below. Runs over the loaded/migrated outlines only (procedural and
        // this-run's fresh rolls aren't in the dict yet), and reads the still-pristine vanilla clones in
        // profileQuests. Idempotent and a no-op on a clean profile; repairs are saved with the rest.
        outlineVerification.VerifyAndFix(profileId, outlines, profileQuests);

        int editedCount = 0;
        int rebuiltCount = 0;
        foreach (var (questId, quest) in profileQuests)
        {
            // THE RUNTIME PATH. A quest this profile has already rolled is REBUILT from its stored
            // decisions - `quest` is a fresh vanilla clone, so regeneration lays the decisions over
            // today's vanilla data, today's weapon-category membership and today's wording. That is what
            // makes a config or content change reach quests rolled long ago, with no re-roll.
            //
            // Deliberately BEFORE any XP call: regeneration applies the multiplier itself (from the
            // current config), so applying it here as well would double it.
            if (regeneration != null && outlines.TryGetValue(questId, out var storedOutline))
            {
                regeneration.Regenerate(quest, storedOutline);
                // Text is a SECOND pass over the same outline - structure and wording are independent
                // consumers of the decisions, so neither has to carry the other's state.
                QuestLocaleGeneration.GenerateFromOutline(storedOutline, quest, workspace);
                rebuiltCount++;
                continue;
            }

            // Admin force-reroll bypasses BOTH whitelists (and, via RollQuest's force flag below, the
            // blacklist) so a normally-untouched quest still gets rolled.
            bool forced = forceRerollIds != null && forceRerollIds.Contains(questId);
            if (!forced)
            {
                // A skipped quest is served as vanilla, so it gets the XP multiplier here - the rebuild
                // and roll paths both get it from regeneration instead.
                if (Settings.Config.EnableTraderWhitelist && !Settings.Config.TraderWhitelist.Contains(quest.TraderId))
                {
                    QuestTools.ChangeXPOnQuest(quest, Settings.Config.QuestXPMultiplier);
                    continue;
                }
                if (Settings.Config.EnableQuestWhitelist && !Settings.QuestWhitelist.Contains(questId))
                {
                    QuestTools.ChangeXPOnQuest(quest, Settings.Config.QuestXPMultiplier);
                    continue;
                }
            }

            // The outline-native roller reads the vanilla quest and returns only the decisions;
            // regeneration then builds the real quest from them - the same builder the rebuild path
            // above uses. Roll against a CLONE so the roller can never disturb the vanilla quest that
            // regeneration reads straight after.
            var outline = questRoller.RollQuest(cloner.Clone(quest)!, force: forced);

            regeneration!.Regenerate(quest, outline);
            QuestLocaleGeneration.GenerateFromOutline(outline, quest, workspace);
            // Store the outline even when EMPTY. An empty outline is a real decision - "this quest was
            // rolled and chosen to be left as vanilla" - and freezing it is what keeps the quest stable
            // across restarts. An unstored empty would let the quest re-roll its edit-chance every boot,
            // silently changing requirements under a player mid-progress.
            outlines[questId] = outline;
            editedCount++;
        }

        // generatedIds is shared across ALL profiles (that's the point - same quest slot, same id
        // for everyone), so two DIFFERENT profiles generating concurrently must not mutate it at the
        // same time. That's now handled inside IdStore: every GetOrCreate/Save here takes its lock,
        // and GetOrCreate is idempotent per key, so concurrent profiles converge on the same ids.
        int generatedCount = GenerateProceduralQuests(profileId, workspace, profileQuests, outlines);

        // Runs on the FULL profileQuests and every time this profile loads - matches the TS original's
        // own ordering/scope. This deliberately means equivalents aren't "baked into" the cache: if
        // another mod registers a new equivalent later (or weaponCompatibility.jsonc gets a new entry),
        // every profile picks it up on its very next load, no cache invalidation needed - HashSet.Add is
        // a harmless no-op for ones a quest already has.
        LoadWeaponEquivalents(profileQuests, BuildGroupConditionIds(outlines));

        // Tag every non-vanilla quest with "(M)" in the served locale - both our procedural quests and
        // other mods' quests, in one place. Must run AFTER GenerateProceduralQuests (procedural names are
        // read back from workspace.Changes here) and BEFORE the locale merge below, which reads
        // workspace.Changes.
        MarkModdedQuests(workspace, profileQuests);

        // Give every objective still missing its map a "[Map]" suffix (see AppendMapSuffixes). Over the
        // FULL served table and AFTER all generation, so it only fills the gaps the outline/procedural
        // passes left: find objectives, and objectives on quests served vanilla. Writes into the same
        // workspace, so it reaches the journal and the web UI through the one locale-overlay path.
        QuestLocaleGeneration.AppendMapSuffixes(profileQuests, workspace);

        // workspace.Changes is deliberately sparse (only the ids we actually touched this run, all
        // regenerated from outlines, plus the modded-quest name tags just added) and English-only. Store
        // it as-is; GetLocaleDbPatch overlays it onto whatever language a client requests, on demand,
        // instead of us cloning every one of the game's ~17 locales up front here.
        QuestrandomizerContext.SetLocaleEditsForProfile(profileId, workspace.Changes);

        QuestrandomizerContext.SetQuestsForProfile(profileId, profileQuests);

        // Heal the one-session desync on any wounded quest (ours or vanilla): objectives all show done
        // but the counters reset to 0, blocking hand-in. Restores each counter the game already recorded
        // complete and strips the one-session flag off the served copy so it can't re-stick. A no-op on a
        // clean profile. Runs AFTER SetQuestsForProfile deliberately - it mutates the SAME served quest
        // objects the context now holds, so its flag strip reaches what the client is served.
        oneSessionQuestRepair.Repair(profileId, profileQuests);

        // Save any newly-created generated-quest ids back to disk so they stay stable across
        // restarts. Cheap no-op write once every profile has triggered generation at least once
        // (GetOrCreate only ever adds entries, never changes existing ones). IdStore.Save takes its
        // own internal lock, since the ids and their file are shared across profiles.
        generatedIds.Save();
        SaveProfileOutlineData(profileId, outlines);
        // The legacy full-quest cache (if this was a migration boot, or a leftover from an older build)
        // has now done its one job; delete it so the profile is genuinely outline-only and nothing ever
        // reads a stale quests.json again. No-op once it's gone.
        DeleteLegacyCacheFiles(profileId);

        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer] Generated quest set for profile {profileId}: {editedCount} newly randomized, "
            + $"{rebuiltCount} rebuilt from outlines, {generatedCount} newly generated.",
            LogTextColor.Cyan);
    }

    // The 3 generated-quest chains (weapon mastery, sniper, handover/fetch) all link together
    // into ONE combined prerequisite sequence - "previousQuest" deliberately carries over between
    // all three loops below, matching the TS original (LeavesQuestManager.generateQuests). Each loop
    // checks `outlines` (already stored + this run's own authored ones) before authoring, same caching
    // principle as the main quest loop above.
    private int GenerateProceduralQuests(MongoId profileId, LocaleWorkspace workspace, Dictionary<MongoId, Quest> profileQuests,
        Dictionary<MongoId, QuestOutline> outlines)
    {
        int generatedCount = 0;
        MongoId questTrader = Settings.Config.QuestGenTrader;
        MongoId previousQuest = default;

        // OUTLINE-NATIVE procedural generation (same two-source model as edited vanilla quests): a
        // procedural quest whose outline is already stored is REBUILT from it; one without is AUTHORED
        // fresh (the only place a die is cast) and its outline stored. Either way the finished quest is
        // built by BuildProceduralSkeleton + regeneration + the locale passes - never served from a
        // frozen copy - so a config or wording change reaches procedural quests at the next boot too.
        // The three chains share `previousQuest` across all loops, one combined prerequisite sequence.

        for (int i = 0; i < Settings.Config.QuestGenTotalWeaponQuests; i++)
        {
            MongoId questId = questGeneration.GetOrCreateQuestId("QG_WeaponMastery", i);
            if (!outlines.TryGetValue(questId, out QuestOutline? outline))
            {
                // The weapon requirement is the same roll a vanilla kill quest makes - one weighted
                // category with mod requirements, or (on a chance) two mod-less categories.
                List<string> categories = questRoller.RollWeaponRequirement();
                outline = questGeneration.AuthorKillQuestOutline(
                    "QG_WeaponMastery", previousQuest, questTrader, i, 5 + i * 2, categories, false);
                outlines[questId] = outline;
                generatedCount++;
            }
            BuildProceduralQuest(outline, questId, workspace, profileQuests);
            previousQuest = questId;
        }

        string weaponGroup = Settings.Config.QuestGenSniperWeaponGroup;
        for (int i = 0; i < Settings.Config.QuestGenTotalSniperQuests; i++)
        {
            MongoId questId = questGeneration.GetOrCreateQuestId("QG_Sniper", i);
            if (!outlines.TryGetValue(questId, out QuestOutline? outline))
            {
                // A sniper quest pins ONE configured weapon category and a distance floor.
                outline = questGeneration.AuthorKillQuestOutline(
                    "QG_Sniper", previousQuest, questTrader, i, 10, new List<string> { weaponGroup }, true);
                outlines[questId] = outline;
                generatedCount++;
            }
            BuildProceduralQuest(outline, questId, workspace, profileQuests);
            previousQuest = questId;
        }

        for (int i = 0; i < Settings.Config.QuestGenTotalHandoverQuests; i++)
        {
            MongoId questId = questGeneration.GetOrCreateQuestId("QG_Fetch", i);
            if (!outlines.TryGetValue(questId, out QuestOutline? outline))
            {
                outline = questGeneration.AuthorHandoverQuestOutline("QG_Fetch", previousQuest, questTrader, i);
                outlines[questId] = outline;
                generatedCount++;
            }
            BuildProceduralQuest(outline, questId, workspace, profileQuests);
            previousQuest = questId;
        }

        return generatedCount;
    }

    // The RNG-FREE rebuild of a procedural quest from its outline - run every boot for both a freshly
    // authored outline and a stored one, mirroring the stored-outline rebuild path the vanilla loop
    // uses. Skeleton (the stand-in for a missing vanilla DB row) -> structure -> base + objective text.
    private void BuildProceduralQuest(QuestOutline outline, MongoId questId, LocaleWorkspace workspace,
        Dictionary<MongoId, Quest> profileQuests)
    {
        ProceduralHeader header = outline.Procedural!;
        Quest quest = questGeneration.BuildProceduralSkeleton(questId, header);
        regeneration!.Regenerate(quest, outline);
        questGeneration.GenerateProceduralBaseLocale(workspace, header, questId);
        QuestLocaleGeneration.GenerateFromOutline(outline, quest, workspace);
        profileQuests[questId] = quest;
    }

    // Combine BOTH equivalence sources - config/weaponCompatibility files and everything other mods
    // registered at runtime through QuestrandomizerCompatibility - and flatten the whole graph into
    // its transitive closure. Called once in LateOnLoad; the result is cached in
    // transitiveWeaponEquivalents and shared by every consumer.
    private Dictionary<MongoId, List<MongoId>> BuildTransitiveWeaponEquivalents()
    {
        // Same pruning intent as FinalizeCategories: a compat file (or another mod) can declare
        // equivalences for weapons whose mod isn't installed here. Unlike category membership, an
        // equivalent pair is never checked against the live item database anywhere else, so an
        // equivalent pointing at an absent mod's weapon used to survive all the way into the handbook
        // and re-roll web UI - where it can't resolve to a locale name and renders as a raw item id
        // (e.g. a "SIG MCX .300 Blackout" listing an EpicsAIO/spys-JSOC-SWR reskin we don't run). By
        // this PostSptModLoader stage every installed mod's items exist, so a missing endpoint
        // genuinely means "that weapon's mod isn't installed" - drop the whole pair.
        var itemDB = databaseService.GetItems();

        var directPairs = new List<(MongoId Original, MongoId Equivalent)>();
        foreach (var entry in Settings.WeaponEquivalents)
        {
            if (!itemDB.ContainsKey(entry.OriginalWeaponId) || !itemDB.ContainsKey(entry.EquivalentWeaponId))
            {
                continue;
            }
            directPairs.Add((entry.OriginalWeaponId, entry.EquivalentWeaponId));
        }
        foreach (var (original, equivalent) in compatibility.Equivalents)
        {
            if (!itemDB.ContainsKey(original) || !itemDB.ContainsKey(equivalent))
            {
                continue;
            }
            directPairs.Add((original, equivalent));
        }
        return QuestrandomizerCompatibility.BuildTransitiveEquivalents(directPairs);
    }

    // C# equivalent of the TS original's LeavesQuestManager.loadWeaponEquivalents - adds every
    // registered equivalent weapon (from config/weaponCompatibility.jsonc AND anything other mods
    // registered at runtime via QuestrandomizerCompatibility) onto every quest's kill-condition
    // weapon requirement that already includes the original weapon. E.g. a "kill using M4A1" quest
    // also accepts a shiny M4A1 from another mod, once that mod registers the equivalence.
    // The KillsConditionIds of every objective this profile's outline defines PURELY by SELECTOR-based
    // weapon categories. Equivalence must NOT touch these: such a category's membership is a DB query
    // (caliber/class/stat), so a shiny/modded gun that belongs is already a direct member, and expanding
    // by equivalence would only drag in weapons the query deliberately excludes (a 9x19 platform-
    // equivalent of a 5.56 member leaking into a "5.56" category).
    //
    // An objective is left OUT (keeps its equivalents) if ANY category it references is hand-listed or
    // mixed (SkipsEquivalence == false) - a shiny of a hand-picked gun isn't a direct member, so
    // equivalence is the only thing that makes it count. Objectives defined by PINNED specific weapons
    // are never here (they're like a vanilla "kill with M4A1" - a shiny M4A1 SHOULD count), and neither
    // are untouched vanilla objectives.
    private HashSet<MongoId> BuildGroupConditionIds(Dictionary<MongoId, QuestOutline> outlines)
    {
        var groupConditionIds = new HashSet<MongoId>();
        foreach (var outline in outlines.Values)
        {
            foreach (var objective in outline.Objectives)
            {
                if (objective.Kills == null || objective.KillsConditionId == null)
                {
                    continue;
                }
                if (objective.Kills.WeaponCategories is not { Count: > 0 } categoryNames)
                {
                    continue;
                }

                // Skip equivalence only when EVERY referenced category is purely selector-based. A single
                // hand-listed/mixed category (or one that no longer exists, so the objective fell back to
                // its vanilla requirement) means equivalence stays on for the whole objective.
                bool allSelectorBased = true;
                foreach (var categoryName in categoryNames)
                {
                    if (!Settings.ResolvedCategories.TryGetValue(categoryName, out var resolved) || !resolved.SkipsEquivalence)
                    {
                        allSelectorBased = false;
                        break;
                    }
                }
                if (allSelectorBased)
                {
                    groupConditionIds.Add(objective.KillsConditionId.Value);
                }
            }
        }
        return groupConditionIds;
    }

    private void LoadWeaponEquivalents(Dictionary<MongoId, Quest> profileQuests, HashSet<MongoId> groupConditionIds)
    {
        // Transitively flattened (see the field's comment): a condition that already lists a real
        // member weapon picks up not just its direct equivalents but everything down the chain, so a
        // shiny clone of a modded gun counts even though it's a second-order equivalent.
        var equivalentsByOriginal = transitiveWeaponEquivalents;

        if (equivalentsByOriginal.Count == 0)
        {
            return;
        }

        foreach (var quest in profileQuests.Values)
        {
            foreach (var task in quest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
            {
                if (task.ConditionType != "CounterCreator" || task.Counter?.Conditions == null)
                {
                    continue;
                }

                foreach (var subCondition in task.Counter.Conditions)
                {
                    if (subCondition.Weapon == null)
                    {
                        continue;
                    }

                    // A weapon-GROUP objective is authoritative as-is - equivalence would only pull in
                    // weapons the category excludes. Pinned/vanilla weapon requirements aren't in this
                    // set and still get their equivalents below. See BuildGroupConditionIds.
                    if (subCondition.Id != null && groupConditionIds.Contains(subCondition.Id.Value))
                    {
                        continue;
                    }

                    // Snapshot before iterating - we may add to subCondition.Weapon below, and
                    // HashSet<T> throws on mutation during enumeration (see ShinyAirdropGuns'
                    // FixQuests for the same pattern/reasoning).
                    foreach (var originalWeapon in subCondition.Weapon.ToList())
                    {
                        if (!equivalentsByOriginal.TryGetValue(originalWeapon, out var newWeapons))
                        {
                            continue;
                        }

                        foreach (var newWeapon in newWeapons)
                        {
                            subCondition.Weapon.Add(newWeapon.ToString());
                        }
                    }
                }
            }
        }
    }

    // Prefix every NON-VANILLA quest's name with "(M) " in this profile's served locale, so players (and
    // the web UI, which keys off the prefix to know a name has no real tarkov.dev page) can tell them from
    // vanilla quests. This is the SINGLE place the marker is applied - both our own procedural quests and
    // OTHER mods' quests come through here (QuestGeneration.SetBaseQuestLocale deliberately writes the
    // plain name and leaves the marking to us). English-only (the marked English name is served for every
    // language, like the rest of the mod's text) and idempotent (skips a name already marked).
    //
    // The two populations get their name from different sources: a procedural quest's name lives only in
    // this run's workspace edits (SetBaseQuestLocale just wrote it - it has no vanilla locale row), while
    // another mod's quest name comes from the served locale DB. So read the workspace first, then fall
    // back to the locale DB.
    private void MarkModdedQuests(LocaleWorkspace workspace, Dictionary<MongoId, Quest> profileQuests)
    {
        var proceduralIds = generatedIds.ValuesSnapshot();
        foreach (var (questId, _) in profileQuests)
        {
            bool isProcedural = proceduralIds.Contains(questId);
            if (!isProcedural)
            {
                // Vanilla or another mod's quest. Without the vanilla list we can't tell which apart, so
                // leave it untouched rather than risk mislabeling every quest as modded.
                if (vanillaQuestIds.Count == 0 || vanillaQuestIds.Contains(questId))
                {
                    continue;
                }
            }

            string localeId = $"{questId} name";
            string? name;
            if (!workspace.Changes.TryGetValue(localeId, out name))
            {
                name = Leaves.Utils.Instance.getLocale("en", questId.ToString(), " name");
            }
            if (name == null || name.StartsWith("(M) "))
            {
                continue;
            }
            workspace.Edit(localeId, $"(M) {name}");
        }
    }
}
