using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer;

// C# equivalent of the TS original's LeavesQuestrandomizerCompatibility - lets OTHER mods
// register "these two weapon ids count as the same weapon for quest purposes" at runtime (e.g.
// ShinyAirdropGuns registering its shiny M4A1 as equivalent to the real M4A1), on top of whatever
// config/weaponCompatibility.jsonc already declares.
//
// Singleton (via DI) so every mod that injects this constructor-parameter-style gets the SAME
// instance - a mod calls AddWeaponEquivalent from its own OnLoad, and by the time any PROFILE
// actually generates its quests (lazily, on that profile's first request - see Questrandomizer.
// EnsureProfileDataGenerated), every mod's OnLoad has already run to completion. Unlike the old TS
// version's DI-token resolve-by-string-name dance, another mod here just needs a constructor
// parameter of type QuestrandomizerCompatibility (and a ProjectReference to this assembly, same
// as everything already references LeavesUtils).
[Injectable(InjectionType.Singleton)]
public class QuestrandomizerCompatibility
{
    private readonly List<(MongoId Original, MongoId Equivalent)> equivalents = new();

    // Wired once by Questrandomizer's own OnLoad (to the handbook generation's description-text
    // writer). Load order between mods isn't guaranteed, so registrations can arrive both before
    // and after the wiring - see SetEquivalentAddedHandler below for how both cases end up
    // handled identically. Only ever touched during the single-threaded OnLoad startup phase, so
    // no locking needed.
    private Action<MongoId, MongoId>? onEquivalentAdded;

    public IReadOnlyList<(MongoId Original, MongoId Equivalent)> Equivalents => equivalents;

    public void AddWeaponEquivalent(MongoId originalWeapon, MongoId equivalentWeapon)
    {
        equivalents.Add((originalWeapon, equivalentWeapon));
        onEquivalentAdded?.Invoke(originalWeapon, equivalentWeapon);
    }

    // Replays everything already registered, then keeps forwarding new registrations live - so a
    // mod that loaded BEFORE Questrandomizer (and registered early) and one that loads after both
    // get the same treatment, without either needing to care about load order.
    internal void SetEquivalentAddedHandler(Action<MongoId, MongoId> handler)
    {
        onEquivalentAdded = handler;
        foreach (var (original, equivalent) in equivalents)
        {
            handler(original, equivalent);
        }
    }

    // Given every DIRECT (Original -> Equivalent) pair from BOTH sources (config/weaponCompatibility
    // files AND runtime registrations), return, for EVERY weapon, the full set of weapons that
    // transitively count as it. Equivalence CHAINS: a modded SVD registered as an equivalent of the
    // vanilla SVD is one hop, and a shiny clone of that modded SVD (registered by ShinyAirdropGuns as
    // an equivalent of the MODDED gun) is a second hop - shiny -> moddedSVD -> vanillaSVD. Every
    // consumer used to follow only a single hop, so a shiny of a weapon that itself only exists in the
    // system as an equivalent (never a first-class category/quest member) attached to nothing - it
    // never joined the vanilla gun's category (e.g. Shturman's arsenal) or counted for its quests.
    // Flattening the chain here, once, and routing every consumer through the result fixes that at any
    // depth. Each node maps to ALL its descendants (so vanillaSVD -> {moddedSVD, shiny} AND
    // moddedSVD -> {shiny}), which lets a category/quest member at ANY level pick up everything below
    // it. Cycle-guarded (a weapon is never listed as its own equivalent) and deduped.
    public static Dictionary<MongoId, List<MongoId>> BuildTransitiveEquivalents(
        IEnumerable<(MongoId Original, MongoId Equivalent)> directPairs)
    {
        // Immediate adjacency: original -> its directly-declared equivalents (deduped, order kept).
        var direct = new Dictionary<MongoId, List<MongoId>>();
        foreach (var (original, equivalent) in directPairs)
        {
            if (!direct.TryGetValue(original, out var list))
            {
                list = new List<MongoId>();
                direct[original] = list;
            }
            if (!list.Contains(equivalent))
            {
                list.Add(equivalent);
            }
        }

        var closure = new Dictionary<MongoId, List<MongoId>>();
        foreach (var original in direct.Keys)
        {
            var collected = new List<MongoId>();
            var seen = new HashSet<MongoId>();

            // Breadth-first from this node's direct equivalents outward, so the flattened list keeps
            // a stable "direct first, then their descendants" order. seen doubles as the cycle guard.
            var queue = new Queue<MongoId>();
            foreach (var equivalent in direct[original])
            {
                queue.Enqueue(equivalent);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current == original)
                {
                    continue; // a cycle led back to the root - a weapon is not its own equivalent
                }
                if (!seen.Add(current))
                {
                    continue; // already collected (also breaks any cycle between descendants)
                }
                collected.Add(current);

                if (direct.TryGetValue(current, out var next))
                {
                    foreach (var child in next)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            closure[original] = collected;
        }

        return closure;
    }
}
