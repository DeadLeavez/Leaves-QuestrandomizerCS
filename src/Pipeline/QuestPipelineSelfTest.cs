using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_Questrandomizer.Pipeline;

// Proves stage 3 (regeneration) reproduces what the roller actually produced - the payoff of the
// post-hoc extractor being able to capture an outline from the EXISTING roller.
//
// The trick that makes this a real proof rather than a smoke test: both sides come from the SAME
// roll. The roller randomizes a quest; the extractor writes down the decisions it just made;
// regeneration replays those decisions onto a fresh vanilla clone. There is no second roll, so the
// dice cannot differ - therefore ANY difference is a regeneration bug, and the comparison is exact
// rather than statistical.
//
// Runs only behind a config flag, and never mutates anything the server goes on to use: it works on
// its own clone and its own throwaway locale workspace. Leaving it off costs nothing.
public class QuestPipelineSelfTest(
    QuestRegeneration regeneration,
    ICloner cloner,
    JsonUtil jsonUtil)
{
    private int questsChecked;
    private int questsIdentical;
    private int questsDiffering;
    // Locale text is tracked SEPARATELY from quest structure, because the two are produced by
    // different code and can be wrong independently. Comparing only the quest JSON left every line of
    // generated objective text unverified - the harness looked green while text was untested.
    private int textLinesChecked;
    private int textLinesIdentical;
    private readonly List<string> textExamples = new();
    private readonly Dictionary<string, int> differencesByPath = new();
    private readonly List<string> examples = new();

    // Fields that are expected to differ and would drown out real signal. Kept deliberately tiny -
    // every entry here is a hole in the proof, so anything added needs a reason.
    private static readonly string[] IgnoredPathFragments =
    {
        // Nothing yet. Real differences should be fixed, not filtered.
    };

    // Procedural quests have no vanilla original, so they are rebuilt from a skeleton + outline and
    // diffed against the scratch they were authored from. Two families of field are EXPECTED to
    // differ there and are not builder bugs:
    private static readonly string[] ProceduralIgnoredFragments =
    {
        // Reward ids (the reward's own id, its target, the wrapper item _id) are minted fresh every
        // build. They are unpinned on purpose - rewards are granted on completion, not keyed by id in
        // profile progress the way conditions are - so churn there is harmless. The reward ITEM tpl is
        // the pinned decision and is verified by construction (the skeleton is handed header.RewardItem)
        // and visible in the preview file. The XP reward VALUE also differs: generated quests now pick
        // up questXPMultiplier via regeneration, which the old generate-and-cache path skipped.
        "/rewards"
    };

    private int proceduralChecked;
    private int proceduralIdentical;
    private readonly Dictionary<string, int> proceduralDiffsByPath = new();
    private readonly List<string> proceduralExamples = new();

    // rolledWorkspace is the REAL workspace the roller just wrote this quest's text into. It holds
    // every quest's text, so only the ids our own regeneration produced are compared against it.
    public void Compare(MongoId questId, Quest rolledQuest, Quest vanillaQuest, LocaleWorkspace rolledWorkspace)
    {
        questsChecked++;

        // Regenerate onto our OWN clone with a throwaway workspace, so the self-test can never leak
        // into the quest data or locale text the server actually serves.
        var candidate = cloner.Clone(vanillaQuest)!;
        var scratchWorkspace = new LocaleWorkspace();
        var outline = LastOutline;
        if (outline == null)
        {
            return;
        }
        regeneration.Regenerate(candidate, outline);
        QuestLocaleGeneration.GenerateFromOutline(outline, candidate, scratchWorkspace);
        CompareText(questId, scratchWorkspace, rolledWorkspace);

        var differences = new List<string>();
        using var expected = JsonDocument.Parse(jsonUtil.Serialize(rolledQuest, false) ?? "{}");
        using var actual = JsonDocument.Parse(jsonUtil.Serialize(candidate, false) ?? "{}");
        DiffElement("", expected.RootElement, actual.RootElement, differences, IgnoredPathFragments);

        if (differences.Count == 0)
        {
            questsIdentical++;
            return;
        }

        questsDiffering++;
        foreach (var difference in differences)
        {
            string path = difference.Split(' ')[0];
            differencesByPath[path] = differencesByPath.GetValueOrDefault(path) + 1;
        }
        if (examples.Count < 15)
        {
            examples.Add($"{questId}: {differences[0]}");
        }
    }

    // Every locale line regeneration produced must match what the roller wrote for the same id. Only
    // ids regeneration emitted are checked: the real workspace also holds every OTHER quest's text.
    private void CompareText(MongoId questId, LocaleWorkspace regenerated, LocaleWorkspace rolled)
    {
        foreach (var (id, regeneratedText) in regenerated.Changes)
        {
            textLinesChecked++;
            string? rolledText = null;
            rolled.Changes.TryGetValue(id, out rolledText);
            if (string.Equals(rolledText, regeneratedText, StringComparison.Ordinal))
            {
                textLinesIdentical++;
                continue;
            }
            if (textExamples.Count < 10)
            {
                textExamples.Add(rolledText == null
                    ? $"{questId} {id}: MISSING in rolled | regen='{Shorten(regeneratedText)}'"
                    : $"{questId} {id}: rolled='{Shorten(rolledText)}' regen='{Shorten(regeneratedText)}'");
            }
        }
    }

    private static string Shorten(string text)
    {
        text = text.ReplaceLineEndings(" ");
        return text.Length <= 100 ? text : text[..100] + "...";
    }

    // Set by the caller immediately before Compare - the outline captured from the roll being checked.
    public QuestOutline? LastOutline { get; set; }

    // Verifies a PROCEDURAL quest: `rebuilt` is BuildProceduralSkeleton + Regenerate applied to the
    // outline authored from `rolled`, so the two must agree everywhere except the intended reward
    // differences above. Same exact-comparison guarantee as Compare - both sides are one roll.
    public void CompareProcedural(MongoId questId, Quest rolled, Quest rebuilt)
    {
        proceduralChecked++;
        var differences = new List<string>();
        using var expected = JsonDocument.Parse(jsonUtil.Serialize(rolled, false) ?? "{}");
        using var actual = JsonDocument.Parse(jsonUtil.Serialize(rebuilt, false) ?? "{}");
        DiffElement("", expected.RootElement, actual.RootElement, differences, ProceduralIgnoredFragments);

        if (differences.Count == 0)
        {
            proceduralIdentical++;
            return;
        }
        foreach (var difference in differences)
        {
            string path = difference.Split(' ')[0];
            proceduralDiffsByPath[path] = proceduralDiffsByPath.GetValueOrDefault(path) + 1;
        }
        if (proceduralExamples.Count < 15)
        {
            proceduralExamples.Add($"{questId}: {differences[0]}");
        }
    }

    // Structural JSON comparison. Reports the PATH of each mismatch rather than dumping both blobs,
    // because with ~460 quests the useful output is "which field is wrong, and how often".
    private static void DiffElement(string path, JsonElement expected, JsonElement actual, List<string> differences, string[] ignored)
    {
        if (differences.Count > 40)
        {
            return; // one quest being wildly wrong shouldn't produce megabytes of log
        }
        if (ignored.Any(path.Contains))
        {
            return;
        }

        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add($"{path} kind {expected.ValueKind}!={actual.ValueKind}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in expected.EnumerateObject())
                {
                    if (!actual.TryGetProperty(property.Name, out var actualValue))
                    {
                        differences.Add($"{path}/{property.Name} missing-in-regenerated");
                        continue;
                    }
                    DiffElement($"{path}/{property.Name}", property.Value, actualValue, differences, ignored);
                }
                foreach (var property in actual.EnumerateObject())
                {
                    if (!expected.TryGetProperty(property.Name, out _))
                    {
                        differences.Add($"{path}/{property.Name} extra-in-regenerated");
                    }
                }
                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                if (expectedItems.Count != actualItems.Count)
                {
                    differences.Add($"{path} length {expectedItems.Count}!={actualItems.Count}  rolled={Trim(expected)} regen={Trim(actual)}");
                    return;
                }
                for (int i = 0; i < expectedItems.Count; i++)
                {
                    DiffElement($"{path}[{i}]", expectedItems[i], actualItems[i], differences, ignored);
                }
                break;

            default:
                if (expected.ToString() != actual.ToString())
                {
                    differences.Add($"{path} value  rolled={Trim(expected)} regen={Trim(actual)}");
                }
                break;
        }
    }

    // Values are what actually diagnose a mismatch; paths alone only say where to look.
    private static string Trim(JsonElement element)
    {
        string raw = element.ToString().ReplaceLineEndings(" ");
        return raw.Length <= 90 ? raw : raw[..90] + "...";
    }

    public void Report()
    {
        if (proceduralChecked > 0)
        {
            double proceduralPass = proceduralIdentical * 100.0 / proceduralChecked;
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] PIPELINE SELF-TEST (procedural): {proceduralIdentical}/{proceduralChecked} skeleton+regenerated identically ({proceduralPass:F1}%), reward ids/XP excluded.",
                proceduralIdentical == proceduralChecked ? LogTextColor.Green : LogTextColor.Yellow);
            foreach (var (path, count) in proceduralDiffsByPath.OrderByDescending(entry => entry.Value).Take(10))
            {
                Leaves.Utils.Instance.printColor($"[Questrandomizer]   {count,5}x  {path}", LogTextColor.Yellow);
            }
            foreach (var example in proceduralExamples.Take(6))
            {
                Leaves.Utils.Instance.printColor($"[Questrandomizer]   e.g. {example}", LogTextColor.Yellow);
            }
        }

        if (questsChecked == 0)
        {
            return;
        }

        double pass = questsIdentical * 100.0 / questsChecked;
        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer] PIPELINE SELF-TEST: {questsIdentical}/{questsChecked} quests regenerated identically ({pass:F1}%), {questsDiffering} differ.",
            questsDiffering == 0 ? LogTextColor.Green : LogTextColor.Yellow);

        foreach (var (path, count) in differencesByPath.OrderByDescending(entry => entry.Value).Take(15))
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer]   {count,5}x  {path}", LogTextColor.Yellow);
        }
        foreach (var example in examples.Take(8))
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer]   e.g. {example}", LogTextColor.Yellow);
        }

        if (textLinesChecked > 0)
        {
            double textPass = textLinesIdentical * 100.0 / textLinesChecked;
            Leaves.Utils.Instance.printColor(
                $"[Questrandomizer] PIPELINE SELF-TEST (locale text): {textLinesIdentical}/{textLinesChecked} lines identical ({textPass:F1}%).",
                textLinesIdentical == textLinesChecked ? LogTextColor.Green : LogTextColor.Yellow);
            foreach (var example in textExamples.Take(6))
            {
                Leaves.Utils.Instance.printColor($"[Questrandomizer]   text e.g. {example}", LogTextColor.Yellow);
            }
        }
    }
}
