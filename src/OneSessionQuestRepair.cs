using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;

namespace Leaves_Questrandomizer;

// ── ONE-SESSION QUEST REPAIR ─────────────────────────────────────────────────────────────────────
//
// Heals the "one-session desync": a handful of vanilla quests are one-session ("visit these places
// AND extract alive, all in ONE raid" - Scout, The Bunker, Colleagues - Part 1, Nothing Fishy About
// This, ...). Every such condition is a CounterCreator whose live TaskConditionCounter resets to 0 at
// each raid end. If all the conditions were satisfied in one raid their ids get written into the
// quest's CompletedConditions (which is what renders the green ✔ in the journal), but the very next
// raid-end wipes the counters back to 0 - and the client gates TURN-IN readiness on the live counter,
// not on CompletedConditions. The result: every objective shows done, yet the Complete button stays
// greyed out and the quest can never be handed in.
//
// This used to be a mod-only wound (adding an objective to a one-session quest made it impossible to
// finish in one raid). It's since become clear it also strikes quests THIS MOD never touched - a
// vanilla one-session quest that desynced on its own on the live server, especially under Fika coop,
// lands in the exact same stuck state. So this repair is deliberately NOT gated on "did we modify it":
// it fixes any wounded quest, ours or vanilla.
//
// SCOPE - only quests already showing the wound. The detection signature ("a condition sits in
// CompletedConditions but its live counter is below target") cannot arise in healthy play: a
// non-one-session counter only ever climbs and persists, so it never reads below a target the game
// already flagged complete. That precision is what lets this run over EVERY one-session quest without
// relaxing the ones a player is completing normally - a healthy one-session quest never matches, so
// its "do it in one raid" behaviour is left fully intact.
//
// For each wounded quest it does two things, together:
//   1. Restores each stuck counter the game already recorded complete back up to its target. It only
//      ever RAISES a counter whose id is in CompletedConditions, so it can never invent progress the
//      player didn't earn.
//   2. Strips the one-session flag off that quest's served conditions, so a later raid-end won't reset
//      the counters straight back to 0 and re-stick it. profileQuests is the exact table handed to the
//      per-profile context, so this reaches what the client is served this session.
//
// It is self-dormant: a healed counter sits at/above target, so on the next boot the signature no
// longer matches and every check below no-ops. Runs on the boot path (GenerateProfileData) and can be
// re-run on demand from the web UI's "Verify quests" button, so a quest that re-desyncs mid-session
// can be healed without a server restart.
[Injectable]
public class OneSessionQuestRepair(
    ProfileHelper profileHelper)
{
    // profileQuests: the profile's SERVED quest definitions (the per-profile table in the context).
    // Returns the ids of the quests it actually healed - empty when the profile was already clean - so
    // callers can name them for a log line or a web-UI message.
    public List<MongoId> Repair(MongoId profileId, Dictionary<MongoId, Quest> profileQuests)
    {
        var healedQuests = new List<MongoId>();

        var pmc = profileHelper.GetPmcProfile(profileId);
        if (pmc == null || pmc.Quests == null)
        {
            return healedQuests;
        }

        var counters = pmc.TaskConditionCounters;

        foreach (var questStatus in pmc.Quests)
        {
            // Only a quest still awaiting hand-in can be stuck this way: a Success/Fail one is finished,
            // an unaccepted one has no progress to restore.
            if (questStatus.Status != QuestStatusEnum.Started && questStatus.Status != QuestStatusEnum.AvailableForFinish)
            {
                continue;
            }

            var questId = questStatus.QId;
            if (!profileQuests.TryGetValue(questId, out var quest))
            {
                continue;
            }
            var conditions = quest.Conditions?.AvailableForFinish;
            if (conditions == null)
            {
                continue;
            }
            var completed = questStatus.CompletedConditions;
            if (completed == null || completed.Count == 0)
            {
                continue;
            }

            // Pass 1 - find the wound. Collect every counter-style condition the game already recorded
            // complete (its id sits in CompletedConditions) whose live counter has fallen back below
            // target. Nothing is changed yet: a quest with no such condition is healthy and must be left
            // exactly as it is, one-session flag and all.
            var toHeal = new List<(MongoId ConditionId, double Target)>();
            foreach (var condition in conditions)
            {
                // Only counter-style objectives reset; a handover/find condition has no live counter.
                if (condition.ConditionType != "CounterCreator")
                {
                    continue;
                }
                var conditionId = condition.Id;

                // The guard that keeps this from inventing progress: restore ONLY what the game itself
                // already flagged complete.
                if (!completed.Contains(conditionId.ToString()))
                {
                    continue;
                }

                // A counter condition needs at least 1; never let a missing/zero target read as "done".
                double target = condition.Value.HasValue && condition.Value.Value > 0 ? condition.Value.Value : 1;

                double current = 0;
                if (counters != null && counters.TryGetValue(conditionId, out var existing) && existing != null && existing.Value.HasValue)
                {
                    current = existing.Value.Value;
                }
                // Already at/above target - this condition is fine, so it isn't part of any wound.
                if (current >= target)
                {
                    continue;
                }

                toHeal.Add((conditionId, target));
            }

            if (toHeal.Count == 0)
            {
                continue; // healthy quest - do NOT touch its one-session behaviour
            }

            // Pass 2 - heal. This quest is genuinely wounded, so restore each stuck counter to target...
            if (counters == null)
            {
                counters = new Dictionary<MongoId, TaskConditionCounter>();
                pmc.TaskConditionCounters = counters;
            }
            foreach (var (conditionId, target) in toHeal)
            {
                // Mirror the exact shape SPT itself writes: keyed by the condition id, .Id the same, .Type
                // CounterCreator, .SourceId the owning quest. SourceId matters - SPT prunes any counter
                // whose SourceId is null (RemoveDanglingTaskConditionCounters), so omitting it would let
                // the heal be swept away again.
                counters[conditionId] = new TaskConditionCounter
                {
                    Id = conditionId,
                    Type = "CounterCreator",
                    Value = target,
                    SourceId = questId
                };
            }

            // ...and strip the one-session flag off the whole served quest so the next raid-end can't
            // reset these counters back to 0 and re-stick it. Whole-quest, matching how QuestRegeneration
            // clears the flag on quests it modifies.
            foreach (var condition in conditions)
            {
                condition.OneSessionOnly = false;
            }

            healedQuests.Add(questId);
        }

        if (healedQuests.Count > 0)
        {
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] One-session repair: healed {healedQuests.Count} stuck quest(s) for profile {profileId}.",
                LogTextColor.Yellow);
        }

        // Deliberately does NOT save the profile itself - the heal is a live mutation of the profile SPT
        // already holds, so the server persists it on its own schedule (raid end, logout, periodic
        // autosave, shutdown). Forcing a save here would mean reasoning about SPT's own save lifecycle
        // from outside it, which we'd rather not own. Worst case a save is missed before a hard kill - the
        // boot-path repair simply re-heals on the next start, so nothing is ever permanently stuck.
        return healedQuests;
    }
}
