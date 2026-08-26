using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer;

// One weapon category, fully compiled into every shape its consumers need - built once by
// Settings from the raw WeaponCategoryEntry, immutable afterward (same
// shared-across-profiles safety rule as everything else on settings).
//
//  - Quest generation copies Weapons/CompiledRequiredGroups/CompiledForbiddenMods straight onto
//    a kill condition (see QuestTools.AddWeaponGroup).
//  - Locale text and handbook sheets read RequiredMods/ForbiddenMods - the human-readable
//    category NAMES, not the compiled id soup.
public class ResolvedWeaponCategory
{
    public required string Name { get; init; }
    public required double Weight { get; init; }

    // Starts as the hand-listed weapons; categories with selectors (calibers / fire-rate) get
    // their matches merged in by Settings.ResolveDynamicWeapons - which runs at
    // the PostSptModLoader stage, so this is the ONE deliberate exception to "immutable after
    // construction". It's still fully settled (single-threadedly, during startup) long before
    // any profile generates quests.
    public required List<MongoId> Weapons { get; init; }

    // True when the raw category declared NO weapon sources at all (no weapons list, no caliber or
    // fire-rate selector) - the deliberate "any weapon" shape, e.g. the "Suppressed" category
    // (empty weapons + a required silencer = any gun with a silencer). Such a category is meant to
    // be empty, so FinalizeCategories must NOT drop it - unlike a category that HAD weapons/selectors
    // which all pruned away because their mod isn't installed.
    public required bool AnyWeapon { get; init; }

    // The raw selector inputs, carried over so ResolveDynamicWeapons can process them late.
    public required List<string> Calibers { get; init; }
    public required List<MongoId> ExcludeWeapons { get; init; }

    // Optional fire-rate filter (e.g. rpm > 1000) applied late in ResolveDynamicWeapons - it NARROWS
    // the caliber candidate set, or filters the whole weapon pool if the category has no calibers.
    // See Data/FireRateBound.
    public required Data.FireRateBound? FireRate { get; init; }

    // True when the RAW category declared an explicit weapons list (before selectors added anything).
    // Needed because Weapons above ends up holding both hand-listed and selector-matched ids, so it
    // can't tell the two apart on its own. Drives SkipsEquivalence below.
    public required bool HasHandListedWeapons { get; init; }

    // Whether equivalence must NOT be applied to this category (its members, its quest conditions, its
    // handbook/web-UI display). True only for a PURELY selector-defined category: membership is a DB
    // query, so a shiny/modded gun that belongs is already a direct member, and pulling in equivalents
    // would only drag in guns the query deliberately excludes (a 9x19 platform-equivalent of a 5.56
    // member leaking into a "5.56" category). A hand-listed category - or a MIXED one that also lists
    // explicit weapons - keeps equivalence: a shiny of a hand-picked gun is not a direct member and
    // equivalence is the only thing that makes it count. (Pinned/vanilla weapon requirements always
    // keep equivalence too - that's decided per objective, not here; see Questrandomizer.BuildGroupConditionIds.)
    public bool SkipsEquivalence
    {
        get
        {
            return HasSelectors && !HasHandListedWeapons;
        }
    }

    // THE central "does this category use any dynamic selector" check on the RESOLVED shape (the raw
    // WeaponCategoryEntry.HasSelectors is its pre-resolve twin). The resolve loop and SkipsEquivalence
    // both key off it - adding a new selector kind is a one-line change here, mirroring the raw side.
    public bool HasSelectors
    {
        get
        {
            return Calibers.Count > 0 || FireRate != null;
        }
    }

    // Mod-category names, straight from the config - for text.
    public required List<string> RequiredMods { get; init; }
    public required List<string> ForbiddenMods { get; init; }

    // The client-facing shape for weaponModsInclusive: OR-of-AND groups. "Big Silencers AND
    // Bipods" compiles to every (silencer, bipod) pairing as its own group - the client counts
    // the kill if the weapon matches ANY group, i.e. carries one mod from each required
    // category. This cartesian product is exactly what the old merge-flag machinery produced;
    // it's just built by one obvious loop now (see Settings.CompileCategory).
    public required List<List<string>> CompiledRequiredGroups { get; init; }

    // The client-facing shape for weaponModsExclusive: every forbidden mod as its own
    // single-element group, deduped - the only shape vanilla data ever uses.
    public required List<List<string>> CompiledForbiddenMods { get; init; }
}
