using System.Text.Json.Serialization;
using SPTarkov.Server.Core.Models.Common;

namespace Leaves_Questrandomizer.Data;

// Mirrors assets/data/misc.jsonc.
public record MiscData
{
    [JsonPropertyName("validMaps")]
    public required List<string> ValidMaps { get; set; }

    // Used for "easy" quests (see config.jsonc's easierQuestList) - a friendlier subset of
    // validMaps.
    [JsonPropertyName("easyMaps")]
    public required List<string> EasyMaps { get; set; }

    [JsonPropertyName("locationIdMap")]
    public required Dictionary<string, string> LocationIdMap { get; set; }

    [JsonPropertyName("validTargets")]
    public required List<ValidTargetEntry> ValidTargets { get; set; }

    // Each inner list is one selectable group of body parts (a kill quest picks ONE whole group,
    // not individual parts) - e.g. ["Chest","LeftArm","RightArm"] means "any hit counts as long as
    // it's chest or an arm", not three separate requirements.
    [JsonPropertyName("bodyParts")]
    public required List<List<string>> BodyParts { get; set; }

    [JsonPropertyName("rewards")]
    public required RewardPoolsData Rewards { get; set; }
}

// One selectable kill target. `Difficulty` drives the kill-count adjustment: when a quest's
// target is rerolled, the count scales by the DIFFERENCE in difficulty between the old and new
// target (harder -> fewer kills, easier -> more). Equal-difficulty targets (Usec/Bear by
// default) score no change. Difficulties don't have to be contiguous - spacing them further
// apart (e.g. a hardcore run that makes PMCs difficulty 5) makes PMC<->Scav rerolls swing the
// count harder in both directions. `Weight` is the odds of this target being picked vs the
// others (plain weighted random) - independent of difficulty, so you can make a hard target
// rare without making it "closer" on the difficulty scale.
public record ValidTargetEntry
{
    [JsonPropertyName("target")]
    public required string Target { get; set; }

    [JsonPropertyName("difficulty")]
    public required int Difficulty { get; set; }

    // Non-required (defaults to 1) so a target entry can omit it and just get normal odds.
    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1;
}

public record RewardPoolsData
{
    [JsonPropertyName("common")]
    public required List<MongoId> Common { get; set; }

    [JsonPropertyName("rare")]
    public required List<MongoId> Rare { get; set; }

    [JsonPropertyName("epic")]
    public required List<MongoId> Epic { get; set; }
}
