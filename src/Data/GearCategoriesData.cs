using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Data;

// Mirrors assets/data/gearcategories.jsonc - the RAW file shape only. Each named group ("armor_fort",
// "headwear_caps", ...) is a set of interchangeable gear pieces: a quest that rolls the group asks the
// player to wear ANY ONE of its items. Groups are picked by weight, exactly like weapon categories.
//
// The display label ("FORT body armor") lives right here on the group, in the same file as the group
// definition - the mod is English-only, so there is nothing to translate and no reason to keep it in a
// separate locale file. (It briefly lived under localization/<lang>/gearCategories.json during the
// multi-language era; that was reverted when translations were dropped.)
public record GearCategoriesData
{
    [JsonPropertyName("gearGroups")]
    public required Dictionary<string, GearCategoryEntry> GearGroups { get; set; }
}

public record GearCategoryEntry
{
    // The human-readable label shown in the objective line and handbook ("FORT body armor"). Falls back
    // to the raw group key if omitted, so a group with no label still renders (visibly un-labelled)
    // rather than blank.
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }


    // Weighted-random pick weight vs the other gear groups - the "weight" half of a weighted pick.
    // Optional: leaving it out means 1 (normal odds).
    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1;

    // Which equipment slot these pieces occupy (armor/headwear/facecover/eyewear/rig/mixed).
    // Informational only for now - kept so the data documents itself and any future slot-aware logic
    // has it to hand.
    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    // The interchangeable pieces. Regeneration expands the group into one single-item requirement per
    // piece ("wear any one of these").
    [JsonPropertyName("items")]
    public List<MongoId> Items { get; set; } = new();
}
