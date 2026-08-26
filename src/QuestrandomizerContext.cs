using System.Collections.Concurrent;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_Questrandomizer;

// ============================================================================================
// THE BIG IDEA - read this before touching anything else in this mod
// ============================================================================================
//
// SPT normally has ONE quest database shared by every player on the server - there's a single
// Dictionary<questId, Quest> object living in memory, and every player's game client reads from
// that same object. This mod wants each player to see their OWN randomized version of the
// quests instead. That's the whole point of Questrandomizer.
//
// The tricky part: every player's game client talks to the server over HTTP, and the server
// (Kestrel/ASP.NET Core, same as any modern .NET web app) can genuinely process MULTIPLE
// players' requests AT THE SAME TIME on different background threads. This is not a "rare edge
// case" - the moment two people are playing together (which is the whole point of Fika coop),
// it's the normal case, all the time.
//
// A naive approach would be: "when player A's request comes in, overwrite the shared quest
// dictionary with player A's version. Let SPT's normal code read it. When player B's request
// comes in, overwrite it again with player B's version." That's exactly what the old TS version
// of this mod did, and it's dangerous here: if player A's and player B's requests overlap in
// time even slightly, player B's overwrite can happen WHILE player A's request is still reading
// - so player A can end up looking at player B's quests, or the two swaps can stomp on each
// other. Nothing crashes, it just silently serves the wrong data to the wrong person.
//
// The fix used here is a built-in .NET tool called AsyncLocal<T>. Think of it as a labelled
// storage box that automatically exists ONCE PER INCOMING REQUEST, not once for the whole
// server. Two requests running at the same time each get their OWN private box, even though
// they're both using the exact same AsyncLocal<T> variable in the code and even if .NET happens
// to run them on the same physical thread at different moments. Setting a value in one request's
// box can never leak into or overwrite a different, concurrently-running request's box. (This is
// the same mechanism ASP.NET Core itself uses internally to make sure the HttpContext for
// request A never gets confused with the HttpContext for request B.)
//
// So the actual plan, in two steps:
//   1. The moment we find out which profile a request belongs to (its session id - see
//      Questrandomizer.cs, which listens for every incoming request), we write that profile's id
//      into the AsyncLocal box below. This happens fresh, correctly, for every single request.
//   2. Later, when SPT's own code asks the database "give me the quests"
//      (DatabaseService.GetQuests()), a small patch (Patches/GetQuestsPatch.cs) checks that same
//      box. If it says "this request belongs to profile X" and we have a randomized quest set
//      saved for profile X, the patch hands back THAT dictionary instead of the shared one -
//      just for the object returned to THIS request. Nothing shared ever gets overwritten, so
//      there's nothing for two players' requests to race over.
public static class QuestrandomizerContext
{
    // The AsyncLocal box itself. There only needs to be ONE AsyncLocal<T> instance for the whole
    // mod (declaring it "static" just means "don't make a new box-factory per class instance,
    // share the one factory") - the actual per-request isolation comes from AsyncLocal<T> itself,
    // not from anything about this field being static.
    private static readonly AsyncLocal<MongoId?> currentProfileId = new();

    // Read or write the CURRENT request's profile id. Safe to call from anywhere, including from
    // inside the static patch method in GetQuestsPatch.cs - Harmony patch methods have to be
    // static, so they can't use normal constructor-based dependency injection to reach a "nicer"
    // instance-based service. A plain static property like this is how they reach shared state.
    public static MongoId? CurrentProfileId
    {
        get => currentProfileId.Value;
        set => currentProfileId.Value = value;
    }

    // Every profile's own randomized quest table, keyed by profile id. Unlike CurrentProfileId
    // above, this dictionary genuinely IS shared across every request - so it's a
    // ConcurrentDictionary, .NET's built-in "safe for many threads at once" dictionary. A plain
    // Dictionary written from two requests at the same moment doesn't just lose one write - it
    // can corrupt its internal state permanently ("Operations that change non-concurrent
    // collections must have exclusive access"), which happened for real: the game client fires
    // its trader-screen requests in PARALLEL, three of them hit this at the same millisecond,
    // and the locale map below got corrupted. The generation work itself is additionally
    // serialized per profile in Questrandomizer.EnsureProfileDataGenerated, but the maps must be
    // concurrent-safe regardless since DIFFERENT profiles can store their results simultaneously.
    private static readonly ConcurrentDictionary<MongoId, Dictionary<MongoId, Quest>> questsByProfile = new();

    public static void SetQuestsForProfile(MongoId profileId, Dictionary<MongoId, Quest> quests)
    {
        questsByProfile[profileId] = quests;
    }

    public static bool TryGetQuestsForProfile(MongoId profileId, out Dictionary<MongoId, Quest>? quests)
    {
        return questsByProfile.TryGetValue(profileId, out quests);
    }

    // Same idea as questsByProfile above, but for locale text. IMPORTANT (this is the thing that
    // tripped up the first version of the proof-of-concept): the quest journal in-game does NOT
    // display Quest.QuestName. It displays whatever locale text is stored under the key
    // "<questId> name" - QuestName is really just an internal label the SERVER uses in its own
    // log messages (this project's other mods already do exactly that - see e.g. ShinyAirdropGuns
    // logging "New Weapon Array for Quest: {quest.QuestName}" - never sent to the client). So
    // swapping the quest data alone was never going to change what shows up on screen; the locale
    // dictionary has to get swapped too.
    //
    // The mod is ENGLISH-ONLY, so we store just ONE sparse edit set per profile - id -> English text,
    // only the ids this mod changed - NOT a full locale and NOT one copy per language. The full served
    // locale for a given language is built lazily from these edits (see builtLocaleByProfile below).
    private static readonly ConcurrentDictionary<MongoId, Dictionary<string, string>> localeEditsByProfile = new();

    public static void SetLocaleEditsForProfile(MongoId profileId, Dictionary<string, string> edits)
    {
        localeEditsByProfile[profileId] = edits;
    }

    public static bool TryGetLocaleEditsForProfile(MongoId profileId, out Dictionary<string, string>? edits)
    {
        return localeEditsByProfile.TryGetValue(profileId, out edits);
    }

    // NOTE: there is deliberately NO cross-request cache of the full merged locale. GetLocaleDbPatch
    // rebuilds the served dictionary (vanilla locale + this profile's sparse edits) fresh on every call and
    // lets it be collected afterwards. The merged dictionary is a ~5MB clone of SPT's own locale and is pure
    // derived data, so keeping one resident per (profile, language) was wasted memory to save a rebuild on a
    // path the game client hits once per startup. See the comment in GetLocaleDbPatch for the full reasoning
    // and what to do instead if GetLocaleDb ever turns out to be hot within a single request.

    // Forget a profile's cached quest + locale data entirely, so the very next request for it (or an
    // explicit re-generation) rebuilds from disk instead of serving the stale in-memory copy. Used by
    // the runtime re-roll flow (see Questrandomizer.RerollProfileQuests): after the disk cache has been
    // trimmed, the profile is evicted here so GenerateProfileData repopulates from the trimmed cache.
    public static void RemoveProfile(MongoId profileId)
    {
        questsByProfile.TryRemove(profileId, out _);
        localeEditsByProfile.TryRemove(profileId, out _);
    }
}
