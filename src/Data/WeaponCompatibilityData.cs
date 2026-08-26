using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Data;

// Mirrors one file in config/weaponCompatibility/ - user-editable list of "these two weapon ids
// count as the same weapon for quest purposes" (e.g. a shiny M4A1 from another mod counts as the
// real M4A1). Every *.jsonc in that folder is loaded and merged (see
// Settings.LoadWeaponEquivalentFiles), so per-mod compat can live in its own file.
public record WeaponCompatibilityFile
{
    [JsonPropertyName("equivalents")]
    public required List<WeaponEquivalentEntry> Equivalents { get; set; }

    // Optional: extra weapons to merge into weapon categories - either an existing one from
    // weaponcategories.jsonc (e.g. dropping a mod's HK guns into "HK Series") or a brand-new
    // category created on the spot. Keeps ALL of one mod's compatibility - equivalents AND
    // category membership - in this single file, while weaponcategories.jsonc stays the curated
    // category definitions. Merged at the final load stage, so ids whose mod isn't installed are
    // pruned and a category left empty is excluded automatically (see
    // Settings.ApplyCategoryAdditions / FinalizeCategories).
    //
    // Non-`required` with a default so compat files written before this key existed still load.
    [JsonPropertyName("weaponCategoryAdditions")]
    public Dictionary<string, List<MongoId>> CategoryAdditions { get; set; } = new();

    // Optional: extra MOD ids (suppressors, bipods, optics, sights...) to merge into an existing
    // mod category from weaponcategories.jsonc. Without this, a quest that asks for "a silencer"
    // wouldn't count a modded suppressor - i.e. it breaks LEGITIMATE play, not just cheese.
    // Unlike weaponCategoryAdditions, unknown names are NOT created: a mod category only means
    // anything if a weapon category references it by name, so a typo here is a mistake.
    //
    // Non-`required` with a default, same back-compat reasoning as CategoryAdditions.
    [JsonPropertyName("modCategoryAdditions")]
    public Dictionary<string, List<MongoId>> ModCategoryAdditions { get; set; } = new();

    // Optional: roll weight for a category NAMED in weaponCategoryAdditions. Without this, a category
    // the compat file invents (one not in weaponcategories.jsonc) is created at weight 1 - which,
    // among the ~85 shipped categories, is so rarely rolled you'd almost never see it in-game. Set a
    // weight here (the shipped categories run 0.5-3) to make a mod's own category actually surface.
    // Keyed by the same category name used in weaponCategoryAdditions.
    //
    // Only applies to categories CREATED by compat files - a name that already exists in
    // weaponcategories.jsonc keeps its curated weight (that file stays the single source of truth for
    // the built-in categories; see ApplyCategoryAdditions). When several compat files weight the same
    // invented category, the largest wins. Non-`required` with a default for back-compat.
    [JsonPropertyName("weaponCategoryWeights")]
    public Dictionary<string, double> CategoryWeights { get; set; } = new();
}

public record WeaponEquivalentEntry
{
    [JsonPropertyName("originalWeaponID")]
    public required MongoId OriginalWeaponId { get; set; }

    [JsonPropertyName("equivalentWeaponID")]
    public required MongoId EquivalentWeaponId { get; set; }
}
