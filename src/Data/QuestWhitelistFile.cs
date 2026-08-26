using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Data;

// Mirrors each file under config/questwhitelists/enabled/ - every enabled file's list gets
// concatenated together into one big whitelist (see Settings).
public record QuestWhitelistFile
{
    [JsonPropertyName("whitelist")]
    public required List<MongoId> Whitelist { get; set; }
}
