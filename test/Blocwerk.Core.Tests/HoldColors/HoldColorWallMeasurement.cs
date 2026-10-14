// <copyright file="HoldColorWallMeasurement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text;
using Blocwerk.Core.Holds.Colors;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// Measures the colour estimate on the old holds of a real wall whose colour somebody set: how many get a suggestion
/// and how many of those are the stored colour. Runs only when <c>BLOCWERK_COLOR_EVAL_PG</c> names a database (a copy
/// of real data) and <c>BLOCWERK_COLOR_EVAL_OUT</c> a local folder for the sample cache and the per-hold list; the
/// wall is <c>BLOCWERK_COLOR_EVAL_WALL</c> or the Attic. The stored colours are user input: a mismatch is "estimate
/// says X, stored Y", to be looked at on the photo.
/// </summary>
public class HoldColorWallMeasurement(ITestOutputHelper output)
{
    private static string? Database => Environment.GetEnvironmentVariable("BLOCWERK_COLOR_EVAL_PG");

    private static string? OutDir => Environment.GetEnvironmentVariable("BLOCWERK_COLOR_EVAL_OUT");

    [Fact]
    public async Task MeasureOnTheOldHoldsOfARealWall()
    {
        if (string.IsNullOrWhiteSpace(Database) || string.IsNullOrWhiteSpace(OutDir))
        {
            return;
        }

        var wall = Environment.GetEnvironmentVariable("BLOCWERK_COLOR_EVAL_WALL") ?? "The Attic";
        var samples = await HoldColorSampleCache.LoadAsync(Database, Path.Combine(OutDir, "color-samples.bin"), wall);
        // Each hold is compared with the other holds of known colour on its photo, as the proposer does for a new one.
        var signatures = samples.ToDictionary(s => s.HoldId, s => HoldColorSignature.Of(s.Own, s.Surroundings));
        var rows = samples
            .Select(s => (Sample: s, Estimate: Palette(s, samples, signatures).Refine(HoldColorEstimator.Estimate(s.Own, s.Surroundings), signatures[s.HoldId])))
            .ToList();
        var summary = Summarise(rows);
        output.WriteLine(summary);
        await File.WriteAllTextAsync(Path.Combine(OutDir, "color-summary.txt"), summary);
        await File.WriteAllLinesAsync(Path.Combine(OutDir, "color-holds.csv"), rows.Select(r => Line(r.Sample, r.Estimate)));
    }

    private static HoldColorPalette Palette(HoldColorSample hold, List<HoldColorSample> samples, Dictionary<Guid, HoldColorSignature?> signatures) =>
        new(samples
            .Where(s => s.PanelId == hold.PanelId && s.HoldId != hold.HoldId && signatures[s.HoldId] is not null)
            .Select(s => (signatures[s.HoldId]!, s.Stored)));

    private static string Summarise(List<(HoldColorSample Sample, HoldColorEstimate Estimate)> rows)
    {
        var proposed = rows.Where(r => r.Estimate.Color is not null).ToList();
        var right = proposed.Count(r => r.Estimate.Color == r.Sample.Stored);
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"holds {rows.Count}, proposed {proposed.Count} ({Percent(proposed.Count, rows.Count)}), right {right} ({Percent(right, proposed.Count)})");
        foreach (var stored in rows.GroupBy(r => r.Sample.Stored).OrderByDescending(g => g.Count()))
        {
            var votes = stored.GroupBy(r => r.Estimate.Color ?? "-").OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  stored {stored.Key} ({stored.Count()}): {string.Join(", ", votes)}");
        }

        foreach (var verdict in rows.Where(r => r.Estimate.Color is null).GroupBy(r => r.Estimate.Verdict).OrderByDescending(g => g.Count()))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  unset {verdict.Key}: {verdict.Count()}");
        }

        return text.ToString();
    }

    private static string Line(HoldColorSample s, HoldColorEstimate e)
    {
        var colored = s.Own.Where(p => p.Chroma >= 3.5).ToList();
        var hue = colored.Count == 0 ? 0 : new OklabColor(0, colored.Sum(p => p.A), colored.Sum(p => p.B)).Hue;
        var chroma = colored.Count == 0 ? 0 : colored.Select(p => p.Chroma).Order().ElementAt(colored.Count / 2);
        var light = s.Own.Count == 0 ? 0 : s.Own.Select(p => p.L).Order().ElementAt(s.Own.Count / 2);
        return string.Join(
            ',',
            s.HoldId,
            s.Stored,
            e.Color ?? "-",
            e.Verdict,
            s.Own.Count,
            F(colored.Count / (double)Math.Max(1, s.Own.Count)),
            F(hue),
            F(chroma),
            F(light));
    }

    private static string Percent(int part, int whole) => whole == 0 ? "-" : (100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture) + " %";

    private static string F(double value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
