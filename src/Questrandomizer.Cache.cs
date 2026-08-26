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

// Per-profile persistence + legacy migration (split out of Questrandomizer.cs - same partial class).
// The disk layer core generation sits on: outline load/save, the one-time legacy-cache migration,
// the pristine vanilla-quest-id read, and the shared quest-status predicate. Bottom layer: depends on
// nothing else in the class - core and the web layer only ever call DOWN into it, never the reverse.
public partial class Questrandomizer
{
    // Statuses that mean the player has ENGAGED with a quest - accepted it, is handing it in, has
    // completed it, or failed it. Quests in any of these are off-limits to the bulk "re-roll locked"
    // action, so nothing in progress or already resolved is ever disturbed. Everything else (Locked,
    // AvailableForStart, AvailableAfter, or simply not in the profile's quest list yet) is fair game.
    private static bool IsEngagedStatus(QuestStatusEnum status)
    {
        return status is QuestStatusEnum.Started
                      or QuestStatusEnum.AvailableForFinish
                      or QuestStatusEnum.Success
                      or QuestStatusEnum.Fail
                      or QuestStatusEnum.FailRestartable
                      or QuestStatusEnum.MarkedAsFailed
                      or QuestStatusEnum.Expired;
    }

    // Converts every ENGAGED cached quest to a (weapon/mod-stripped) outline; locked ones are left
    // out so the caller can clear the cache and let them re-roll fresh. Returns the outlines to seed
    // this run with. Persisted at the end of GenerateProfileData like any other outlines.
    private Dictionary<MongoId, QuestOutline> MigrateLegacyCache(
        MongoId profileId, Dictionary<MongoId, Quest> cachedQuests, Dictionary<MongoId, Quest> realQuests)
    {
        var migrated = new Dictionary<MongoId, QuestOutline>();

        var pmc = profileHelper.GetPmcProfile(profileId);
        var engaged = new HashSet<MongoId>();
        foreach (var questStatus in pmc?.Quests ?? new List<QuestStatus>())
        {
            if (IsEngagedStatus(questStatus.Status))
            {
                engaged.Add(questStatus.QId);
            }
        }

        var proceduralIds = generatedIds.ValuesSnapshot();
        int converted = 0, pinned = 0;

        // EVERY engaged quest gets an outline, so none of them can fall through to a fresh roll (which
        // mints new condition ids and detaches the player's in-progress quests). A quest still in the
        // old cache is CONVERTED - its decisions preserved, weapon/mod stripped, ids kept. One that
        // isn't cached but is a quest this mod would manage gets an EMPTY outline, which regenerates to
        // plain vanilla: no disruption, no reroll, no invented requirements. Only LOCKED quests (absent
        // from `engaged`) are left without an outline, so the caller's cache-clear lets them roll fresh.
        var blacklist = new HashSet<MongoId>(Settings.Config.QuestBlacklist);

        foreach (var questId in engaged)
        {
            if (proceduralIds.Contains(questId))
            {
                continue; // procedural quests have no vanilla original - deferred, see pipeline notes
            }

            // Blacklisted quests must NEVER be converted - conversion re-rolls the weapon requirement
            // (the category name can't be read back, so it's rolled fresh), which is exactly the "edit"
            // the blacklist forbids. Note a blacklisted quest CAN reach this branch: the default
            // whitelist is every vanilla quest, so a blacklisted quest is usually whitelisted too, and if
            // it was rolled before being blacklisted it's still in the old cache. Pin it to an empty
            // outline instead: regeneration rebuilds it from vanilla (weapon/target/count all revert),
            // and because randomization edited kill conditions IN PLACE (vanilla condition ids kept), the
            // player's in-progress counter stays attached across the revert.
            if (blacklist.Contains(questId))
            {
                migrated[questId] = new QuestOutline { QuestId = questId };
                pinned++;
                continue;
            }

            if (cachedQuests.TryGetValue(questId, out var cachedQuest))
            {
                //Grab all vanilla quest conditions so we can sift out the modded conditions from the cache.
                var vanillaConditionIds = new HashSet<MongoId>();
                
                if (realQuests.TryGetValue(questId, out var vanillaQuest))
                {
                    foreach (var condition in vanillaQuest.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
                    {
                        vanillaConditionIds.Add(condition.Id);
                    }
                }
                migrated[questId] = QuestOutlineConverter.Convert(questId, cachedQuest, vanillaConditionIds, questRoller);
                converted++;
            }
            else if (Settings.QuestWhitelist.Contains(questId))
            {
                // Whitelisted, engaged, but no cached decisions to preserve (a stale cache, or progress
                // predating it). An empty outline pins it to vanilla so it is not re-rolled out from
                // under the player. It can still be re-rolled deliberately from the web UI later.
                migrated[questId] = new QuestOutline { QuestId = questId };
                pinned++;
            }
            // else: not whitelisted (e.g. a blacklisted or disabled-list quest) - no outline needed,
            // the roll path's whitelist check already serves it as vanilla.
        }

        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer] Migrated profile {profileId} to outlines: {converted} engaged quest(s) converted "
            + $"(weapon/mod requirements re-rolled fresh), {pinned} pinned to vanilla, locked quests will re-roll fresh.",
            LogTextColor.Cyan);
        return migrated;
    }

    private Dictionary<MongoId, QuestOutline> LoadCachedOutlines(MongoId profileId)
    {
        string path = $"assets/generated/{profileId}/outline.jsonc";
        if (!System.IO.File.Exists(System.IO.Path.Combine(modPath, path)))
        {
            return new Dictionary<MongoId, QuestOutline>();
        }
        return modHelper.GetJsonDataFromFile<Dictionary<MongoId, QuestOutline>>(modPath, path);
    }

    // Loads a legacy profile's old full-quest cache (assets/generated/<id>/quests.json) for the one-time
    // outline migration. Returns false - and leaves an empty dict in 'quests' - when there is nothing to
    // migrate: either the file is absent (never had one, or already migrated and deleted) or it
    // deserializes to zero quests. Only the quests half is read now; the paired locales.json that this
    // method used to also return is dead once the outline pipeline regenerates locale from the outline.
    private bool TryLoadLegacyQuestCache(MongoId profileId, out Dictionary<MongoId, Quest> quests)
    {
        quests = new Dictionary<MongoId, Quest>();

        string questsPath = $"assets/generated/{profileId}/quests.json";
        if (!System.IO.File.Exists(System.IO.Path.Combine(modPath, questsPath)))
        {
            return false;
        }

        quests = modHelper.GetJsonDataFromFile<Dictionary<MongoId, Quest>>(modPath, questsPath);
        return quests.Count > 0;
    }

    private void SaveProfileOutlineData(MongoId profileId, Dictionary<MongoId, QuestOutline> outlines)
    {
        // saveJsonFile doesn't create missing parent folders itself, and this profile's folder
        // won't exist yet the very first time it generates.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(modPath, "assets/generated", profileId.ToString()));

        // The outline is the ONLY thing persisted now - the decisions behind the quests. The built
        // quests and their locale are NOT saved: they are regenerated from this file on every boot
        // (proven equivalent), which is what lets a config/content/wording change reach old quests. The
        // old quests.json (~6 MB) + locales.json (~4 MB) per profile are gone; this is ~0.4 MB.
        //
        // Written as JSONC: the annotator appends "// what this id is" to every line ending in one, so
        // the file is actually readable. Safe to read back because SPT's deserializer skips comments.
        string outlineJson = jsonUtil.Serialize(outlines, true) ?? "{}";
        System.IO.File.WriteAllText(
            System.IO.Path.Combine(modPath, $"assets/generated/{profileId}/outline.jsonc"),
            annotator.Annotate(outlineJson));
    }

    // The vanilla quest ids, read from the pristine on-disk database. SPT loads these files into memory
    // at boot and every mod (including this one) mutates only the in-memory copy - the file stays
    // vanilla - so a quest present in the live DB but absent from this set was added by another mod. The
    // web UI's vanilla-names endpoint uses the same on-disk-is-pristine trick for items.
    private HashSet<MongoId> LoadVanillaQuestIds()
    {
        var ids = new HashSet<MongoId>();
        // modPath = <root>/user/mods/Leaves-Questrandomizer -> walk up three for the game root.
        string? root = System.IO.Directory.GetParent(modPath)?.Parent?.Parent?.FullName;
        string file = System.IO.Path.Combine(root ?? "", "SPT_Data", "database", "templates", "quests.json");
        if (!System.IO.File.Exists(file))
        {
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Vanilla quest list not found at {file} - modded quests won't be marked (M) this session.",
                LogTextColor.Yellow);
            return ids;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            ids.Add(new MongoId(property.Name));
        }
        return ids;
    }

    // Removes a profile's legacy full-quest cache once outlines have taken over. quests.json/locales.json
    // are no longer written or read for serving - they exist only as a migration source (see
    // GenerateProfileData), so once that has run they are pure clutter that could only cause confusion.
    private void DeleteLegacyCacheFiles(MongoId profileId)
    {
        foreach (string name in new[] { "quests.json", "locales.json" })
        {
            string path = System.IO.Path.Combine(modPath, $"assets/generated/{profileId}/{name}");
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
