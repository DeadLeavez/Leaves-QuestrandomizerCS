using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_Questrandomizer.Pipeline;

// STAGE 1 of the outline-native pipeline: vanilla quest -> the STRUCTURE the roll builds on.
// (See docs/rolling-decision-tree.md.)
//
// This is the correctly-scoped "vanilla extractor". It emits, for every objective this mod can touch
// (a CounterCreator with a Kills sub-condition, or a HandoverItem), a Vanilla-origin ObjectiveOutline
// SHELL carrying only IDENTITY:
//   - ConditionId, objective Type, and the vanilla sub-condition ids (CounterId / KillsConditionId /
//     LocationConditionId / GearConditionId). These are the PINNED ids every later decision hangs off,
//     so pinning starts here and a decision already has the right ids to reference.
//
// It emits NO values. This is the whole difference from the deleted seeder: count, target, savage,
// maps, etc. are NOT copied in. Those are read on demand by QuestFacts, which falls back to the vanilla
// quest for anything the roll hasn't decided (`outline ?? vanilla`). An empty payload here therefore
// still answers every fact correctly - nothing needs "seeding".
//
// Naming: deliberately NOT "extractor". This reads STRUCTURE out of the pristine vanilla quest BEFORE
// any roll - the opposite of reading rolled DECISIONS back out of a mutated condition afterwards (the
// old post-roll "extractor" bridge, now removed with the pre-outline pipeline).
//
// Sparsity: this over-produces (a shell per touchable objective). The ROLL keeps a shell only if its
// edit gate fires and drops it otherwise, so undecided objectives never reach the persisted outline -
// no external bookkeeping needed. ADDED objectives (the roll's own appended kill/handover) are not
// scaffolded here; the roll mints those with fresh ids.
public static class QuestOutlineScaffold
{
    public static List<ObjectiveOutline> Build(Quest vanilla)
    {
        var shells = new List<ObjectiveOutline>();

        foreach (var task in vanilla.Conditions?.AvailableForFinish ?? new List<QuestCondition>())
        {
            if (task.ConditionType == "CounterCreator" && task.Counter?.Conditions != null)
            {
                var kills = task.Counter.Conditions.FirstOrDefault(condition => condition.ConditionType == "Kills");
                if (kills != null)
                {
                    shells.Add(KillsShell(task, kills));
                }
                // A CounterCreator with no Kills sub-condition (a pure InZone/HealthEffect counter) is
                // not something the roll edits - no shell, regeneration serves it from vanilla.
            }
            else if (task.ConditionType == "HandoverItem")
            {
                shells.Add(HandoverShell(task));
            }
            // FindItem / LeaveItemAtLocation / Skill / ...: never touched, so never scaffolded.
        }

        return shells;
    }

    private static ObjectiveOutline KillsShell(QuestCondition task, QuestConditionCounterCondition kills)
    {
        var conditions = task.Counter!.Conditions!;
        var location = conditions.FirstOrDefault(condition => condition.ConditionType == "Location");
        var gear = conditions.FirstOrDefault(condition => condition.ConditionType == "Equipment");

        return new ObjectiveOutline
        {
            Origin = ObjectiveOrigin.Vanilla,
            Type = ObjectiveType.Kills,
            ConditionId = task.Id,
            CounterId = ParseId(task.Counter.Id),
            KillsConditionId = kills.Id,
            // Null when vanilla has no such sub-condition. If the roll ADDS one (a location/gear that
            // wasn't there), it mints that id itself - the scaffold only records what vanilla already has.
            LocationConditionId = location?.Id,
            GearConditionId = gear?.Id
            // No Kills payload: every value is a decision, read via QuestFacts (`?? vanilla`) until set.
        };
    }

    private static ObjectiveOutline HandoverShell(QuestCondition task)
    {
        return new ObjectiveOutline
        {
            Origin = ObjectiveOrigin.Vanilla,
            Type = ObjectiveType.Handover,
            ConditionId = task.Id
            // No Handover payload, same reason.
        };
    }

    private static MongoId? ParseId(string? raw)
    {
        return string.IsNullOrEmpty(raw) ? (MongoId?)null : new MongoId(raw);
    }
}
