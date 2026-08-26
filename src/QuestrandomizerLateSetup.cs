using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace Leaves_Questrandomizer;

// Second half of Questrandomizer's startup. PostSptModLoader is the FINAL OnLoad stage - by the
// time this runs, every mod on the server (weapon mods included) has finished its own OnLoad, so
// the item database is complete and the dynamic weapon-category selectors can resolve against
// it. The +2 offset puts this after LeavesUtils' own PostSptModLoader-stage locale-cache clear.
// See Questrandomizer.LateOnLoad for what actually happens here - this class exists only
// because a single [Injectable] class can't register two OnLoad hooks at different stages.
[Injectable(TypePriority = OnLoadOrder.PostSptModLoader + 2)]
public class QuestrandomizerLateSetup(Questrandomizer questrandomizer) : IOnLoad
{
    public Task OnLoad()
    {
        questrandomizer.LateOnLoad();
        return Task.CompletedTask;
    }
}
