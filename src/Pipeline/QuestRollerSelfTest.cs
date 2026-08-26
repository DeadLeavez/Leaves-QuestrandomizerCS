using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;

namespace Leaves_Questrandomizer.Pipeline;

// Verifies the outline-native roller (QuestRoller) end to end, without needing a profile. For every
// vanilla quest it rolls an outline, regenerates it TWICE (which must be byte-identical - proving
// regeneration is RNG-free on these outlines, i.e. no per-boot churn), and runs the locale pass. Each
// quest is wrapped so a single bad one is reported, not fatal. Then it logs decision distributions and
// a few sample lines to eyeball against the configured chances.
//
// There is deliberately NO exact diff against the OLD roller: the two draw from Random independently,
// so their outputs can never match. What this proves is that the new roller's outlines are well-formed
// (the real regeneration + locale consume them cleanly), regeneration is deterministic on them, and the
// decision rates are in the right ballpark.
public class QuestRollerSelfTest(
    QuestRoller roller,
    QuestGeneration questGeneration,
    QuestRegeneration regeneration,
    ICloner cloner,
    JsonUtil jsonUtil)
{
    private sealed class Stats
    {
        public int Total;
        public int RegenFailures;
        public int LocaleFailures;
        public int NonDeterministic;

        public int ProceduralTotal;
        public int ProceduralBuildFailures;
        public int ProceduralLocaleFailures;
        public int ProceduralNonDeterministic;

        public int KillObjectives;
        public int HandoverObjectives;
        public int AddedKills;
        public int AddedHandovers;

        public int Weapon;
        public int MultiWeapon;
        public int Location;
        public int Distance;
        public int Time;
        public int BodyPart;
        public int Gear;

        public int HandoverCategory;
        public int HandoverFir;

        public readonly List<string> Failures = new List<string>();
        public readonly List<string> Samples = new List<string>();
    }

    public void Run(Dictionary<MongoId, Quest> vanillaQuests)
    {
        Stats stats = new Stats();

        foreach (KeyValuePair<MongoId, Quest> entry in vanillaQuests)
        {
            stats.Total++;
            MongoId questId = entry.Key;
            Quest vanilla = entry.Value;

            QuestOutline outline;
            try
            {
                outline = roller.RollQuest(cloner.Clone(vanilla)!);
            }
            catch (Exception exception)
            {
                RecordFailure(stats, "ROLL", questId, exception);
                continue;
            }

            TallyDecisions(stats, outline);

            // Determinism: regenerate the SAME outline onto two fresh clones - they must come out equal.
            Quest first = cloner.Clone(vanilla)!;
            Quest second = cloner.Clone(vanilla)!;
            try
            {
                regeneration.Regenerate(first, outline);
                regeneration.Regenerate(second, outline);
            }
            catch (Exception exception)
            {
                stats.RegenFailures++;
                RecordFailure(stats, "REGEN", questId, exception);
                continue;
            }
            if (jsonUtil.Serialize(first, false) != jsonUtil.Serialize(second, false))
            {
                stats.NonDeterministic++;
                if (stats.Failures.Count < 15)
                {
                    stats.Failures.Add($"NON-DETERMINISTIC {questId}");
                }
            }

            // Locale runs against the built quest (same as the live path).
            LocaleWorkspace workspace = new LocaleWorkspace();
            try
            {
                QuestLocaleGeneration.GenerateFromOutline(outline, first, workspace);
            }
            catch (Exception exception)
            {
                stats.LocaleFailures++;
                RecordFailure(stats, "LOCALE", questId, exception);
                continue;
            }
            CollectSample(stats, workspace);
        }

        RunProcedural(stats);

        Report(stats);
    }

    // Verifies the OUTLINE-NATIVE procedural path (QuestGeneration.Author*Outline -> BuildProceduralSkeleton
    // -> Regenerate -> locale), the same way the vanilla loop above is verified: author each configured
    // chain quest as an outline, build it from that outline TWICE (must be byte-identical, proving the
    // rebuild is RNG-free), and run both locale passes. There is no vanilla quest to diff against - a
    // procedural quest is invented - so "well-formed + deterministic" is the whole check.
    private void RunProcedural(Stats stats)
    {
        MongoId trader = Settings.Config.QuestGenTrader;
        MongoId previous = default;

        for (int i = 0; i < Settings.Config.QuestGenTotalWeaponQuests; i++)
        {
            List<string> categories = roller.RollWeaponRequirement();
            QuestOutline outline = questGeneration.AuthorKillQuestOutline(
                "QG_WeaponMastery", previous, trader, i, 5 + i * 2, categories, false);
            VerifyProcedural(stats, outline);
            previous = outline.QuestId;
        }

        string weaponGroup = Settings.Config.QuestGenSniperWeaponGroup;
        for (int i = 0; i < Settings.Config.QuestGenTotalSniperQuests; i++)
        {
            QuestOutline outline = questGeneration.AuthorKillQuestOutline(
                "QG_Sniper", previous, trader, i, 10, new List<string> { weaponGroup }, true);
            VerifyProcedural(stats, outline);
            previous = outline.QuestId;
        }

        for (int i = 0; i < Settings.Config.QuestGenTotalHandoverQuests; i++)
        {
            QuestOutline outline = questGeneration.AuthorHandoverQuestOutline("QG_Fetch", previous, trader, i);
            VerifyProcedural(stats, outline);
            previous = outline.QuestId;
        }
    }

    // Build one authored procedural outline twice and run its locale, tallying failures. Mirrors the
    // per-quest checks in Run, but the base is BuildProceduralSkeleton (a procedural quest has no vanilla
    // row to clone) rather than a vanilla clone.
    private void VerifyProcedural(Stats stats, QuestOutline outline)
    {
        stats.ProceduralTotal++;
        ProceduralHeader? header = outline.Procedural;
        if (header == null)
        {
            stats.ProceduralBuildFailures++;
            RecordFailure(stats, "PROC-HEADER", outline.QuestId, new Exception("authored procedural outline has no header"));
            return;
        }

        Quest first;
        Quest second;
        try
        {
            first = questGeneration.BuildProceduralSkeleton(outline.QuestId, header);
            second = questGeneration.BuildProceduralSkeleton(outline.QuestId, header);
            regeneration.Regenerate(first, outline);
            regeneration.Regenerate(second, outline);
        }
        catch (Exception exception)
        {
            stats.ProceduralBuildFailures++;
            RecordFailure(stats, "PROC-BUILD", outline.QuestId, exception);
            return;
        }
        if (jsonUtil.Serialize(first, false) != jsonUtil.Serialize(second, false))
        {
            stats.ProceduralNonDeterministic++;
            if (stats.Failures.Count < 15)
            {
                stats.Failures.Add($"PROC-NON-DETERMINISTIC {outline.QuestId}");
            }
        }

        LocaleWorkspace workspace = new LocaleWorkspace();
        try
        {
            questGeneration.GenerateProceduralBaseLocale(workspace, header, outline.QuestId);
            QuestLocaleGeneration.GenerateFromOutline(outline, first, workspace);
        }
        catch (Exception exception)
        {
            stats.ProceduralLocaleFailures++;
            RecordFailure(stats, "PROC-LOCALE", outline.QuestId, exception);
            return;
        }
        CollectSample(stats, workspace);
    }

    private static void TallyDecisions(Stats stats, QuestOutline outline)
    {
        foreach (ObjectiveOutline objective in outline.Objectives)
        {
            if (objective.Type == ObjectiveType.Kills)
            {
                stats.KillObjectives++;
                if (objective.Origin == ObjectiveOrigin.Added)
                {
                    stats.AddedKills++;
                }
                KillsOutline? kills = objective.Kills;
                if (kills != null)
                {
                    if (kills.WeaponCategories is { Count: > 0 })
                    {
                        stats.Weapon++;
                    }
                    if (kills.WeaponCategories is { Count: >= 2 })
                    {
                        stats.MultiWeapon++;
                    }
                    if (kills.Maps is { Count: > 0 })
                    {
                        stats.Location++;
                    }
                    if (kills.DistanceValue != null)
                    {
                        stats.Distance++;
                    }
                    if (kills.DaytimeFrom != null)
                    {
                        stats.Time++;
                    }
                    if (kills.BodyParts is { Count: > 0 })
                    {
                        stats.BodyPart++;
                    }
                    if (kills.RequiredGearCategory != null || kills.RequiredGear is { Count: > 0 })
                    {
                        stats.Gear++;
                    }
                }
            }
            else if (objective.Type == ObjectiveType.Handover)
            {
                stats.HandoverObjectives++;
                if (objective.Origin == ObjectiveOrigin.Added)
                {
                    stats.AddedHandovers++;
                }
                HandoverOutline? handover = objective.Handover;
                if (handover != null)
                {
                    if (handover.CategoryName != null)
                    {
                        stats.HandoverCategory++;
                    }
                    if (handover.FoundInRaid == true)
                    {
                        stats.HandoverFir++;
                    }
                }
            }
        }
    }

    private static void RecordFailure(Stats stats, string stage, MongoId questId, Exception exception)
    {
        if (stats.Failures.Count < 15)
        {
            stats.Failures.Add($"{stage} {questId}: {exception.Message}");
        }
    }

    private static void CollectSample(Stats stats, LocaleWorkspace workspace)
    {
        if (stats.Samples.Count >= 8)
        {
            return;
        }
        foreach (KeyValuePair<string, string> line in workspace.Changes)
        {
            if (stats.Samples.Count >= 8)
            {
                return;
            }
            stats.Samples.Add(line.Value);
        }
    }

    private static string Percent(int count, int total)
    {
        if (total == 0)
        {
            return "0%";
        }
        return $"{count * 100 / total}%";
    }

    private void Report(Stats stats)
    {
        bool allClean = stats.RegenFailures == 0 && stats.LocaleFailures == 0 && stats.NonDeterministic == 0
            && stats.ProceduralBuildFailures == 0 && stats.ProceduralLocaleFailures == 0 && stats.ProceduralNonDeterministic == 0;
        LogTextColor headColor = allClean ? LogTextColor.Green : LogTextColor.Yellow;

        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer] ROLLER SELF-TEST: {stats.Total} quests rolled; {stats.RegenFailures} regen fail, "
            + $"{stats.LocaleFailures} locale fail, {stats.NonDeterministic} non-deterministic.",
            headColor);
        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer]   procedural: {stats.ProceduralTotal} authored; {stats.ProceduralBuildFailures} build fail, "
            + $"{stats.ProceduralLocaleFailures} locale fail, {stats.ProceduralNonDeterministic} non-deterministic.",
            headColor);
        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer]   objectives decided: {stats.KillObjectives} kills, {stats.HandoverObjectives} handover "
            + $"({stats.AddedKills} added kills, {stats.AddedHandovers} added handovers).",
            LogTextColor.Cyan);
        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer]   kill decisions: weapon {Percent(stats.Weapon, stats.KillObjectives)} "
            + $"(multi {Percent(stats.MultiWeapon, stats.KillObjectives)}), "
            + $"location {Percent(stats.Location, stats.KillObjectives)}, distance {Percent(stats.Distance, stats.KillObjectives)}, "
            + $"time {Percent(stats.Time, stats.KillObjectives)}, bodypart {Percent(stats.BodyPart, stats.KillObjectives)}, "
            + $"gear {Percent(stats.Gear, stats.KillObjectives)}.",
            LogTextColor.Cyan);
        Leaves.Utils.Instance.printColor(
            $"[Questrandomizer]   handover decisions: category {Percent(stats.HandoverCategory, stats.HandoverObjectives)}, "
            + $"FIR {Percent(stats.HandoverFir, stats.HandoverObjectives)}.",
            LogTextColor.Cyan);

        foreach (string failure in stats.Failures)
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer]   FAIL {failure}", LogTextColor.Red);
        }
        foreach (string sample in stats.Samples)
        {
            Leaves.Utils.Instance.printColor($"[Questrandomizer]   e.g. {sample}", LogTextColor.Magenta);
        }
    }
}
