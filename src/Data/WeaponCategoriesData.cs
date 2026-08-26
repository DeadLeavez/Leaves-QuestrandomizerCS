using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Data;

// Mirrors assets/data/weaponcategories.jsonc - the RAW file shape only. Nothing outside
// Settings should read these records directly: they get compiled into
// ResolvedWeaponCategory (see ResolvedWeaponCategory.cs) at load, and everything downstream
// (quest generation, locale text, handbook sheets) works off the resolved form.
public record WeaponCategoriesData
{
    [JsonPropertyName("categories")]
    public required Dictionary<string, WeaponCategoryEntry> Categories { get; set; }

    [JsonPropertyName("modCategories")]
    public required Dictionary<string, ModCategoryEntry> ModCategories { get; set; }
}

public record WeaponCategoryEntry
{
    // How likely this category is to be picked vs other categories - the "weight" half of a
    // weighted-random pick. Optional since the overhaul: leaving it out means 1 (normal odds),
    // so simple categories stay fully self-contained without weight bookkeeping.
    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1;

    [JsonPropertyName("weapons")]
    public List<MongoId> Weapons { get; set; } = new();

    // Names from ModCategories below. EVERY listed category is required, and any single mod
    // from a category satisfies that category - "requiredMods": ["Big Silencers", "Bipods"]
    // means "a big silencer AND a bipod, whichever ones you like". This replaces the old
    // order-dependent {"name": merge-bool} encoding, which in years of real configs was only
    // ever used to express exactly this.
    [JsonPropertyName("requiredMods")]
    public List<string> RequiredMods { get; set; } = new();

    // Names from ModCategories below. Every mod in every listed category is individually
    // forbidden - a flat union.
    [JsonPropertyName("forbiddenMods")]
    public List<string> ForbiddenMods { get; set; } = new();

    // ==== Dynamic membership selectors - all optional, resolved against the LIVE item database
    // (including other mods' weapons) late in startup, and merged into the hand-listed weapons.
    // NOTE: the client provably IGNORES the quest-condition weaponCaliber field (tested live -
    // an AKM counted on a 7.62x25-only condition), which is exactly why these selectors resolve
    // to explicit weapon id lists instead of caliber conditions.

    // Ammo calibers, written the human way: "7.62x25", "9x19", "12g". Matched tolerantly against
    // each weapon's internal AmmoCaliber ("Caliber762x25TT", "Caliber9x19PARA", "Caliber12g").
    // The only selector that BUILDS the candidate set - see FireRate below, which only narrows it.
    [JsonPropertyName("calibers")]
    public List<string> Calibers { get; set; } = new();

    // Hand-pruning for selector matches you don't want, e.g. one cursed 9x19 weapon that
    // shouldn't count for the 9x19 category.
    [JsonPropertyName("excludeWeapons")]
    public List<MongoId> ExcludeWeapons { get; set; } = new();

    // Optional fire-rate filter, e.g. { "above": 1000 } for "everything over 1000 rpm". It NARROWS:
    // a category with calibers keeps only the caliber matches that also satisfy the fire rate; a
    // category with only a fire-rate filter (no calibers) filters the whole weapon pool. See
    // FireRateBound.
    [JsonPropertyName("fireRate")]
    public FireRateBound? FireRate { get; set; }

    // THE central "does this category use any dynamic selector" check - calibers OR a fire-rate
    // filter. Every consumer that enumerates selector kinds (AnyWeapon, the resolve loop, the
    // resolved category's own HasSelectors/SkipsEquivalence) routes through this, so a change here
    // reaches them all instead of scattered boolean chains.
    // [JsonIgnore] because it's computed, not part of the raw file shape this record mirrors.
    [JsonIgnore]
    public bool HasSelectors
    {
        get
        {
            return Calibers.Count > 0 || FireRate != null;
        }
    }

    // ==== Legacy format (pre-overhaul) - read only so old third-party configs keep working.
    // Settings migrates these into RequiredMods/ForbiddenMods with a deprecation
    // warning. The old bool meant "does this entry AND-merge into the previous one"; real-world
    // usage only ever formed plain AND-chains, which is exactly what RequiredMods expresses.
    [JsonPropertyName("mods-inclusive")]
    public Dictionary<string, bool>? LegacyModsInclusive { get; set; }

    [JsonPropertyName("mods-exclusive")]
    public Dictionary<string, bool>? LegacyModsExclusive { get; set; }
}

// A fire-rate filter on a weapon category: keep only weapons whose full-auto RPM (the item's
// Properties.BFirerate) is within these bounds. Above/Below are STRICT and independent - set either
// or both (both = a band, Above < rpm < Below). A weapon with no BFirerate (a bolt-action, most
// shotguns) has no fire rate and is excluded by any bound that's set. This only ever NARROWS the
// caliber candidate set (or the whole weapon pool when the category has no calibers); it never adds
// weapons on its own.
public record FireRateBound
{
    // Weapon passes when its fire rate is strictly greater than this. Null = no lower bound.
    [JsonPropertyName("above")]
    public double? Above { get; set; }

    // Weapon passes when its fire rate is strictly less than this. Null = no upper bound.
    [JsonPropertyName("below")]
    public double? Below { get; set; }
}

public record ModCategoryEntry
{
    [JsonPropertyName("mods")]
    public required List<MongoId> Mods { get; set; }

    // The legacy "together" flag ("ALL mods in this category required at once") is gone: no
    // config ever set it to true, and dropping it makes every mod category mean the same simple
    // thing - "any one of these mods". Unknown JSON properties are ignored on load, so old
    // files that still carry it won't break.
}
