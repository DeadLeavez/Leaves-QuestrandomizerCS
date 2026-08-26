using System.Reflection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Services;

namespace Leaves_Questrandomizer.Patches;

// Same idea as GetQuestsPatch.cs, targeting a different method: LocaleService.GetLocaleDb(string
// language) is what actually serves the game client its dictionary of display text when it calls
// /client/locale/<language> - this is the thing the quest journal reads quest NAMES from (see the
// big comment on QuestrandomizerContext.localeByProfile for why that matters).
public class GetLocaleDbPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod()
    {
        return typeof(LocaleService).GetMethod(nameof(LocaleService.GetLocaleDb));
    }

    // "string? language" here isn't a random extra parameter - Harmony postfix methods can ask
    // for any of the ORIGINAL method's own parameters just by matching the name and type, and it
    // fills them in with whatever value the caller actually passed. So this lets us see which
    // language the client asked for, in addition to __result (what GetLocaleDb was about to
    // return - see GetQuestsPatch.cs for the full explanation of that part).
    [PatchPostfix]
    private static void Postfix(string? language, ref Dictionary<string, string> __result)
    {
        if (QuestrandomizerContext.CurrentProfileId is not { } profileId)
        {
            return;
        }

        // Does this profile have any generated text? If not, serve vanilla untouched.
        if (!QuestrandomizerContext.TryGetLocaleEditsForProfile(profileId, out var edits) || edits == null)
        {
            return;
        }

        // Build the served locale fresh every call: __result IS the requested language's vanilla locale
        // (shared, must not be mutated). Copy it - a new Dictionary(source) is a full logical clone because
        // string keys/values are immutable - then overlay this profile's small English edit set.
        //
        // We deliberately do NOT keep this merged dictionary around. It's a full clone of SPT's own locale
        // (~5MB for en) and it's pure derived data - vanilla locale + our sparse edits, both of which we
        // already have on hand. Holding one resident per (profile, language) forever was the mod's single
        // biggest memory cost, to save a rebuild on a path the game client hits exactly ONCE per startup.
        // Rebuilding on demand keeps only the tiny edit set (a few KB) resident per player; the big clone is
        // transient and gets collected right after the response is serialized. If a profiler ever shows
        // GetLocaleDb is hot WITHIN a single request, memoize per-request in an AsyncLocal instead of
        // reinstating a cross-request cache - that bounds the cost to in-flight requests, not every profile.
        var built = new Dictionary<string, string>(__result);
        foreach (var (id, text) in edits)
        {
            built[id] = text;
        }

        __result = built;
    }
}
