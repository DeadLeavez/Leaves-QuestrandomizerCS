using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Data;

// Mirrors assets/data/handovercategories.jsonc - this is the RAW file shape, before it gets
// expanded into the actual usable "category name -> every item id in that category" lookup (see
// Settings.BuildHandoverCategories).
public record HandoverCategoriesFile
{
    // Item PARENT category ids - every item in the game whose _parent matches one of these gets
    // auto-collected into that category.
    [JsonPropertyName("categoryWhitelist")]
    public required List<MongoId> CategoryWhitelist { get; set; }

    // Hand-picked item lists that aren't a real parent-category grouping (e.g. "Cigarettes" isn't
    // one BSG item category, it's several specific items someone decided belong together).
    [JsonPropertyName("customCategories")]
    public required Dictionary<string, List<MongoId>> CustomCategories { get; set; }

    // Item ids that must never be asked for in a handover. The outline-verification pass re-rolls any
    // stored handover that landed on one (see OutlineVerification.FixBlacklistedHandover). Stopgap:
    // this whole file is slated for an overhaul, but the flat blacklist covers the need for now.
    [JsonPropertyName("ItemBlacklist")]
    public required List<MongoId> ItemBlacklist { get; set; }
}
