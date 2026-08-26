using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace Leaves_Questrandomizer.Patches;

// A "Harmony patch" lets you change what an EXISTING method does, without editing that method's
// own source code (we don't have SPT's source, only its compiled .dll, so this is the only way).
// Here we're targeting DatabaseService.GetQuests() - the one method the entire rest of the server
// calls whenever it needs "the quest database". See QuestrandomizerContext.cs for the full
// explanation of why we're doing this instead of overwriting the shared quest data directly.
//
// AbstractPatch (from SPTarkov.Reflection.Patching - SPT's own thin, friendlier wrapper around
// the Harmony library) works like this:
//   1. GetTargetMethod() below tells it WHICH method to patch.
//   2. Any method in this class marked [PatchPostfix] gets run automatically, every time,
//      immediately AFTER the real DatabaseService.GetQuests() finishes running.
// "Postfix" specifically means "runs after" - there's also [PatchPrefix] for "runs before", which
// we don't need here since we're not trying to stop the original method from running, just
// change what it hands back afterward.
public class GetQuestsPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return typeof(DatabaseService).GetMethod(nameof(DatabaseService.GetQuests));
    }

    // Harmony postfix methods MUST be static (this is a hard requirement of how Harmony works,
    // not a stylistic choice) - that's exactly why QuestrandomizerContext.CurrentProfileId is
    // also static, it's the only way this method can reach "which profile is this request for".
    //
    // "ref Dictionary<MongoId, Quest> __result" is Harmony's special naming convention: any
    // parameter literally named "__result" (two leading underscores) gets automatically filled in
    // with whatever the original GetQuests() just returned, and because it's "ref", if we assign
    // a NEW dictionary to it here, that's what the original CALLER of GetQuests() actually
    // receives - as far as they can tell, GetQuests() itself returned our swapped-in dictionary.
    [PatchPostfix]
    private static void Postfix(ref Dictionary<MongoId, Quest> __result)
    {
        MongoId? profileId = QuestrandomizerContext.CurrentProfileId;
        if (profileId is not MongoId id)
        {
            // No request is "in flight" right now (or we don't know who it's for) - leave the
            // real, shared quest dictionary alone.
            return;
        }

        if (QuestrandomizerContext.TryGetQuestsForProfile(id, out var profileQuests))
        {
            __result = profileQuests!;
        }
    }
}
