using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;

namespace Leaves_Questrandomizer.Pipeline;

// The OUTLINE-VERIFICATION pass: a place to REPAIR stored outlines before they are regenerated.
//
// Why this exists as its own stage. The outline is the sole source of truth and it is written by two
// very different producers - a fresh roll (QuestRoller) and the one-time legacy-cache migration
// (QuestOutlineConverter). The migration reconstructs an outline from an already-rolled quest and
// cannot always recover the clean shape the roller would have written, so it can leave outlines in
// states the roller never produces. Rather than teach regeneration and locale generation to tolerate
// every such state, this pass runs FIRST, on every boot, and rewrites those specific states into ones
// the rest of the pipeline already handles correctly.
//
// The contract of a fixer here:
//   - It targets ONE explicitly-known bad shape - never a general "validate everything" sweep. If a
//     future data problem turns up, it gets its own named fixer added to VerifyAndFix, and the others
//     are untouched. That is the whole point of the design: a growing list of small, independent repairs.
//   - It must be IDEMPOTENT. This pass runs before regeneration on EVERY generation, and the repaired
//     outline is saved back, so a fixer must turn a bad shape into a good one that it will NOT match
//     again next boot (otherwise it re-fires forever, and any RNG inside it churns the requirement
//     under the player).
//   - It mutates the outline IN PLACE. The caller persists the outlines it passed in, so a mutation
//     here is what makes the repair permanent.
//
// One member here is not a shape fixer but a PURGE (ShouldPurgeBlacklistedUnstarted): it DROPS a whole
// outline rather than repairing it, for a quest that has since been blacklisted on a profile that never
// started it. It follows the same rules (explicit, targeted, idempotent) and its removal is likewise
// made permanent by the caller re-persisting the dictionary it mutated.
public class OutlineVerification(
    QuestRoller roller)
{
    // How many times a blacklisted-handover re-roll may try before giving up. A single item re-roll
    // can land on another blacklisted item (RollTierItem draws from the same tier), so we retry to
    // reach an acceptable pick; this cap just stops a pathological all-blacklisted tier from looping
    // forever - the last roll is then accepted as-is.
    private const int BlacklistRerollAttempts = 25;

    // Runs every fixer over one profile's stored outlines, in place, and returns how many repairs were
    // made (0 on a clean profile - the common case once everyone is migrated). `profileId` is the PMC
    // whose outlines these are, needed by the purge below to read live quest status. `vanillaQuests` are
    // the pristine vanilla clones for this profile, keyed by id, so a fixer can read what a quest
    // originally looked like; an outline with no matching vanilla quest (e.g. a stored procedural one)
    // simply gets a null and each fixer decides what that means for it.
    public int VerifyAndFix(MongoId profileId, Dictionary<MongoId, QuestOutline> outlines, IReadOnlyDictionary<MongoId, Quest> vanillaQuests)
    {
        int totalFixes = 0;

        // Outlines to DROP entirely (the purges below). Collected here and removed after the loop -
        // a dictionary can't be mutated while it is being iterated. Two reasons are tracked separately
        // so each drop logs why it happened.
        List<MongoId> toPurge = new List<MongoId>();
        List<MongoId> toPurgeOldVersion = new List<MongoId>();

        foreach (var (questId, outline) in outlines)
        {
            Quest? vanilla = null;
            if (vanillaQuests.TryGetValue(questId, out var found))
            {
                vanilla = found;
            }

            // PURGES take precedence over the shape fixers: if the whole outline is about to be dropped,
            // there is no point repairing its shape first.
            if (ShouldPurgeBlacklistedUnstarted(profileId, questId, outline))
            {
                toPurge.Add(questId);
                continue;
            }
            if (ShouldPurgeOldVersionUnstarted(profileId, questId, outline))
            {
                toPurgeOldVersion.Add(questId);
                continue;
            }

            // Each explicit fixer gets its turn here. Add new ones to this list as they are written.
            totalFixes += FixMultiItemHandover(outline, vanilla);
            totalFixes += FixBlacklistedHandover(outline);
            totalFixes += FixSpecificGear(outline);
        }

        foreach (MongoId questId in toPurge)
        {
            outlines.Remove(questId);
            totalFixes++;
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Outline purge: quest {questId} is now blacklisted and the player has not "
                + "started it - dropped its stored outline so it reverts to vanilla.",
                LogTextColor.Yellow);
        }

        foreach (MongoId questId in toPurgeOldVersion)
        {
            outlines.Remove(questId);
            totalFixes++;
        }
        // One tally line, not one per quest - a version bump can drop hundreds of outlines at once.
        if (toPurgeOldVersion.Count > 0)
        {
            string version = QuestOutline.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Outline purge: {toPurgeOldVersion.Count} not-yet-started quest(s) were rolled "
                + $"under an older outline version - dropped their outlines so they re-roll fresh under version {version}.",
                LogTextColor.Yellow);
        }

        if (totalFixes > 0)
        {
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Outline verification: applied {totalFixes} repair(s) to stored outlines.",
                LogTextColor.Yellow);
        }
        return totalFixes;
    }

    // FIXER: multi-item handover lists.
    //
    // The roller only ever writes a handover as a SINGLE ItemIds entry or a CategoryName - never a
    // multi-item ItemIds. The only producer of a multi-item list is the legacy migration
    // (QuestOutlineConverter.ConvertHandover), which pins the cached handover's whole expanded Target
    // list verbatim - a vanilla OR-list, or worse a whole handover CATEGORY frozen into its members
    // (Sanitary Standards Part 2's list, for instance, is exactly the "Info" category). Regeneration
    // then serves all of them as accepted items, but the locale renders only the first (it reads
    // ItemIds[0]), so the player sees one item and can hand over several - and the category's
    // late-binding is lost.
    //
    // The repair: re-roll the handover completely through the roller's own FillHandover, exactly as a
    // fresh roll would - it lands on a single tier-picked item or a live-resolving category, both of
    // which regeneration and the locale already render correctly. Idempotent: the outcome is never a
    // multi-item ItemIds, so this never matches the same handover twice.
    private int FixMultiItemHandover(QuestOutline outline, Quest? vanilla)
    {
        int fixes = 0;
        foreach (ObjectiveOutline objective in outline.Objectives)
        {
            if (objective.Type != ObjectiveType.Handover)
            {
                continue;
            }
            HandoverOutline? handover = objective.Handover;
            if (handover == null)
            {
                continue;
            }
            if (handover.ItemIds == null || handover.ItemIds.Count <= 1)
            {
                continue;
            }

            // The tier anchor for the fresh roll: the item the player currently sees (the locale renders
            // ItemIds[0]), so the re-rolled single item lands in a comparable tier.
            MongoId anchor = handover.ItemIds[0];

            // Blank the payload so FillHandover rolls it as if for the first time: no stale multi-item
            // list left behind if the fresh roll picks a category, and no compounding of the migration's
            // already-adjusted count / category multiplier. With Count now null, GetHandoverCount reads
            // the VANILLA count - the same base a first-time DecideHandoverObjective uses.
            handover.CategoryName = null;
            handover.ItemIds = null;
            handover.FoundInRaid = null;
            handover.Count = null;

            double baseCount = 0;
            if (vanilla != null)
            {
                baseCount = QuestFacts.GetHandoverCount(vanilla, objective) ?? 0;
            }

            roller.FillHandover(handover, baseCount, anchor, true);
            fixes++;

            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Outline fix: quest {outline.QuestId} handover {objective.ConditionId} "
                + "carried a multi-item list (migration artifact) - re-rolled it to a single item/category.",
                LogTextColor.Yellow);
        }
        return fixes;
    }

    // FIXER: handover of a blacklisted item.
    //
    // handovercategories.jsonc carries an ItemBlacklist of items that must never be asked for (e.g. a
    // modded "reduced delay" F-1 that shouldn't be a fetch target). A roll can still land on one -
    // RollTierItem picks any item in the anchor's tier - so this pass re-rolls any stored handover
    // whose pinned item is blacklisted, exactly as a fresh roll would (through the roller's own
    // FillHandover), until it lands on something acceptable.
    //
    // Only pinned single/multi ItemIds are checked; a late-bound CategoryName is left alone (its
    // members resolve at regeneration time, and screening categories against the blacklist is a job for
    // the coming handover-data overhaul, not this stopgap). Idempotent in practice: the loop re-rolls
    // until the pinned item is no longer blacklisted (or lands on a category), so it will not re-fire.
    private int FixBlacklistedHandover(QuestOutline outline)
    {
        if (Settings.HandoverItemBlacklist.Count == 0)
        {
            return 0;
        }

        int fixes = 0;
        foreach (ObjectiveOutline objective in outline.Objectives)
        {
            if (objective.Type != ObjectiveType.Handover)
            {
                continue;
            }
            HandoverOutline? handover = objective.Handover;
            if (handover == null)
            {
                continue;
            }
            if (handover.ItemIds == null || !ContainsBlacklistedItem(handover.ItemIds))
            {
                continue;
            }

            // The tier anchor for the re-roll: the (blacklisted) item currently pinned, so the fresh
            // pick lands in a comparable tier. Keep the existing count as the base - only the item is
            // the problem here - and pass skipCount so FillHandover doesn't re-apply the category
            // multiplier on top of it.
            MongoId anchor = handover.ItemIds[0];
            double baseCount = handover.Count ?? 0;

            int attempts = 0;
            do
            {
                // FillHandover only SETS the field it rolls, never clears the other, so blank both first.
                handover.CategoryName = null;
                handover.ItemIds = null;
                roller.FillHandover(handover, baseCount, anchor, true);
                attempts++;
            }
            while (handover.ItemIds != null
                && ContainsBlacklistedItem(handover.ItemIds)
                && attempts < BlacklistRerollAttempts);

            fixes++;

            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Outline fix: quest {outline.QuestId} handover {objective.ConditionId} "
                + "asked for a blacklisted item - re-rolled it to a fresh item/category.",
                LogTextColor.Yellow);
        }
        return fixes;
    }

    // FIXER: required gear pinned as SPECIFIC items instead of a gear group.
    //
    // The pre-categories roller wrote required gear as a set of specific piece ids (KillsOutline
    // .RequiredGear - "wear any one of these"). Gear is category-based now: a roll picks one weighted
    // gear GROUP from gearcategories.jsonc and stores its name, which regeneration late-binds and the
    // locale renders as the group's label. Any stored outline still carrying a specific-item list is
    // re-rolled here into a fresh gear group, exactly as a first-time roll would - dropping the pinned
    // ids and picking a weighted group in their place.
    //
    // Idempotent: it clears RequiredGear as it converts, so a repaired outline no longer matches (a
    // group-based outline has RequiredGear == null). Left alone when no gear groups are defined - there
    // would be nothing to roll, and blanking the requirement silently would be worse than leaving it.
    private int FixSpecificGear(QuestOutline outline)
    {
        if (Settings.GearCategoriesWeighting.Count == 0)
        {
            return 0;
        }

        int fixes = 0;
        foreach (ObjectiveOutline objective in outline.Objectives)
        {
            if (objective.Type != ObjectiveType.Kills)
            {
                continue;
            }
            KillsOutline? kills = objective.Kills;
            if (kills == null)
            {
                continue;
            }
            if (kills.RequiredGear is not { Count: > 0 })
            {
                continue;
            }

            // Drop the pinned pieces and, unless a group was somehow already chosen, roll one in their
            // place. GearConditionId stays as-is: regeneration builds the same condition under it, just
            // from the group's items now.
            kills.RequiredGear = null;
            if (kills.RequiredGearCategory == null)
            {
                kills.RequiredGearCategory = roller.RollGearCategory();
            }
            fixes++;

            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] Outline fix: quest {outline.QuestId} objective {objective.ConditionId} "
                + $"required specific gear pieces (legacy) - re-rolled to gear group '{kills.RequiredGearCategory}'.",
                LogTextColor.Yellow);
        }
        return fixes;
    }

    // PURGE: a stored outline for a quest that has since been added to the quest blacklist, on a profile
    // that never started that quest.
    //
    // Unlike the fixers above this does not repair a shape - it drops the outline whole. The use case:
    // new quests get added to the blacklist (e.g. after they're found to randomize badly), and everyone
    // who hasn't touched them yet should just get the vanilla quest, not the already-rolled randomized
    // one frozen in their outline. Dropping the outline sends the quest back through the roll path, where
    // RollQuest short-circuits a blacklisted quest to an empty outline - so it serves as vanilla and stays
    // stable from then on.
    //
    // Guards, all required:
    //   - The outline actually carries decisions (Objectives.Count > 0). An already-empty outline is
    //     vanilla already, so there is nothing to purge - and skipping it keeps this idempotent: the
    //     re-roll a purge triggers writes back an empty outline, which this then leaves alone next boot.
    //   - The quest is on the blacklist NOW (config drives this; it's why the outline is stale).
    //   - The player has NOT started it. A started/finished/failed quest is left strictly alone: its
    //     randomized conditions may already be baked into live progress, and yanking them mid-quest is
    //     exactly the desync this whole cache design exists to prevent.
    private bool ShouldPurgeBlacklistedUnstarted(MongoId profileId, MongoId questId, QuestOutline outline)
    {
        if (outline.Objectives.Count == 0)
        {
            return false;
        }
        if (!QuestFacts.IsBlacklisted(questId))
        {
            return false;
        }
        if (Leaves.Utils.Instance.hasProfileStartedQuest(profileId, questId))
        {
            return false;
        }
        return true;
    }

    // PURGE: a stored outline rolled under an OLDER outline version, on a profile that has not started
    // that quest. Companion to ShouldPurgeBlacklistedUnstarted - the same drop-and-re-roll mechanism,
    // but the trigger is a version bump (QuestOutline.CurrentVersion) rather than the blacklist.
    //
    // Use case: a mod update adds content a locked quest should pick up (new weapon categories, changed
    // roll odds). Raising QuestOutline.CurrentVersion marks every previously-rolled outline as stale;
    // dropping the outline sends the quest back through the roll path, where a fresh RollQuest writes a
    // new outline stamped with the current version. Everything else is reused untouched.
    //
    // Guards, all required:
    //   - NOT procedural. A mod-invented quest has no vanilla original and lives in a generated chain;
    //     it is re-authored by its own generator, not this path (mirrors the manual re-roll's scope).
    //   - The outline actually carries decisions (Objectives.Count > 0). An empty outline is vanilla
    //     already - nothing to re-roll - and skipping it keeps this idempotent alongside the blacklist
    //     purge (whose triggered re-roll writes an empty outline back).
    //   - The stored version is BELOW the current one. Equal-or-newer is already up to date; a fresh
    //     roll stamps CurrentVersion, so a re-rolled quest never matches again next boot. Compared with
    //     < only, so the double version never runs into equality/representation concerns.
    //   - The player has NOT started it. A started/finished/failed quest keeps its outline: its rolled
    //     conditions may already be baked into live progress, and re-rolling mid-quest is the desync
    //     this cache design exists to prevent - the identical rule to the blacklist purge.
    private bool ShouldPurgeOldVersionUnstarted(MongoId profileId, MongoId questId, QuestOutline outline)
    {
        if (outline.Procedural != null)
        {
            return false;
        }
        if (outline.Objectives.Count == 0)
        {
            return false;
        }
        if (outline.Version >= QuestOutline.CurrentVersion)
        {
            return false;
        }
        if (Leaves.Utils.Instance.hasProfileStartedQuest(profileId, questId))
        {
            return false;
        }
        return true;
    }

    // True if any id in the list is on the handover blacklist.
    private bool ContainsBlacklistedItem(List<MongoId> itemIds)
    {
        foreach (MongoId itemId in itemIds)
        {
            if (Settings.HandoverItemBlacklist.Contains(itemId))
            {
                return true;
            }
        }
        return false;
    }
}
