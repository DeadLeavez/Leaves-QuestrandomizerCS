using System.Text.Json.Serialization;

namespace Leaves_Questrandomizer.Data;

// One row of enemymaps.jsonc: "these bot roles appear on these maps". The file is a LIST of these,
// so several roles that share a map set (the Goons, a boss + its guards) live on one row instead of
// being repeated. Settings.Init expands the list into the flat, case-insensitive role -> map-codes
// lookup the rest of the mod consumes (Settings.EnemyMaps).
//
// Neither field is `required`: an omitted/empty `maps` just means the listed roles resolve to no maps
// and keep their "[Any map]", and both are guarded into empty lists on load. [JsonPropertyName] is
// mandatory - the deserializer is case-sensitive exact-match, so an unannotated field silently stays null.
public record EnemyMapEntry
{
    // Bot role ids (SavageRole, e.g. "bossKilla", "followerBigPipe"), matched case-insensitively.
    [JsonPropertyName("roles")]
    public List<string>? Roles { get; set; }

    // Map codes those roles appear on - the same codes misc.jsonc uses (e.g. "bigmap", "RezervBase").
    [JsonPropertyName("maps")]
    public List<string>? Maps { get; set; }
}
