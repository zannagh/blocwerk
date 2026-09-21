// <copyright file="ProfileProgressionPane.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// Loads and shapes the progression series behind the profile's Progress pane. Lifted verbatim out of
/// the single-file profile page.
/// </summary>
public partial class ProfileProgressionPane
{
    /// <summary>The member whose progression is shown.</summary>
    [Parameter]
    [EditorRequired]
    public Guid TargetUserId { get; set; }

    /// <summary>True when the target is the signed-in viewer, which persists window and grouping.</summary>
    [Parameter]
    public bool IsSelf { get; set; }

    private UserProgression? progression;
    private int boulderGradeSpan = 5;
    private Guid loadedFor;

    // Window/grouping for the other-user view is kept locally: it must not persist onto the viewer's
    // own settings, nor onto the target's. Self view reuses the persisted per-user settings.
    private int otherWindowDays = 90;
    private ProgressionGroupBy otherGroupBy = ProgressionGroupBy.Week;

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (loadedFor == TargetUserId && progression != null)
        {
            return;
        }

        loadedFor = TargetUserId;
        progression = IsSelf
            ? await ProgressionService.GetProgressionAsync()
            : await ProgressionService.GetProgressionForUserAsync(TargetUserId, otherWindowDays, otherGroupBy);
    }

    private async Task OnWindowChange(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out var days))
        {
            return;
        }

        if (IsSelf)
        {
            await ProgressionService.UpdateProgressionWindowAsync(days);
            progression = await ProgressionService.GetProgressionAsync();
        }
        else
        {
            otherWindowDays = days;
            progression = await ProgressionService.GetProgressionForUserAsync(TargetUserId, otherWindowDays, otherGroupBy);
        }
    }

    private async Task OnGroupByChange(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out var value) || !Enum.IsDefined(typeof(ProgressionGroupBy), value))
        {
            return;
        }

        if (IsSelf)
        {
            await ProgressionService.UpdateProgressionGroupingAsync((ProgressionGroupBy)value);
            progression = await ProgressionService.GetProgressionAsync();
        }
        else
        {
            otherGroupBy = (ProgressionGroupBy)value;
            progression = await ProgressionService.GetProgressionForUserAsync(TargetUserId, otherWindowDays, otherGroupBy);
        }
    }

    // ---- chart data builders (mirrors Activity's progression charts) ----
    private List<ChartSeriesPoint> BoulderPoints() => progression!.Buckets.Select(b =>
        new ChartSeriesPoint(
            b.BoulderScore,
            b.BoulderScore.HasValue ? $"{Range(b)} — {b.BoulderScore.Value:F0} / {b.BoulderGrade}" : $"{Range(b)} — no sends",
            b.Label)).ToList();

    private List<ChartSeriesPoint> TrainingPoints() => progression!.Buckets.Select(b =>
        new ChartSeriesPoint(
            b.TrainingScore,
            b.TrainingScore.HasValue ? $"{Range(b)} — {b.TrainingScore.Value:F0}" : $"{Range(b)} — no training",
            b.Label)).ToList();

    // The boulder Y scale shows at least boulderGradeSpan grades centred on the current rating (so
    // "6C" sits amid 6B..7A rather than filling the axis), always keeping the data in view. Dragging
    // the axis changes the span.
    private (double Min, double Max, List<(double Value, string Label)> Ticks) BoulderScale()
    {
        var sorted = GradeScoring.AllScores.OrderBy(kv => kv.Value).ToList();
        if (sorted.Count == 0 || progression == null)
        {
            return (0, 1, []);
        }

        var vals = progression.Buckets.Where(b => b.BoulderScore.HasValue).Select(b => b.BoulderScore!.Value).ToList();
        var center = progression.BoulderScore > 0
            ? progression.BoulderScore
            : (vals.Count > 0 ? vals.Average() : sorted[sorted.Count / 2].Value);

        var nearest = 0;
        var best = double.MaxValue;
        for (var i = 0; i < sorted.Count; i++)
        {
            var d = Math.Abs(sorted[i].Value - center);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        var span = Math.Clamp(boulderGradeSpan, 3, sorted.Count);
        var lo = Math.Clamp(nearest - (span / 2), 0, Math.Max(0, sorted.Count - span));
        var hi = Math.Min(sorted.Count - 1, lo + span - 1);

        double min = sorted[lo].Value;
        double max = sorted[hi].Value;
        if (vals.Count > 0)
        {
            min = Math.Min(min, vals.Min());
            max = Math.Max(max, vals.Max());
        }

        var pad = (max - min) * 0.08;
        if (pad <= 0)
        {
            pad = 100;
        }

        min -= pad;
        max += pad;

        var ticks = sorted.Where(kv => kv.Value >= min && kv.Value <= max)
            .Select(kv => ((double)kv.Value, kv.Key)).ToList();
        return (min, max, ticks);
    }

    private void OnBoulderYScale(int direction)
    {
        // Drag down (+1) widens the range → more grades; drag up (-1) narrows it.
        boulderGradeSpan = Math.Clamp(boulderGradeSpan + direction, 3, 12);
    }

    private List<(double Value, string Label)> TrainingYTicks() => NumericYTicks(TrainingPoints(), v => v.ToString("F0"));

    private static List<(double Value, string Label)> NumericYTicks(List<ChartSeriesPoint> points, Func<double, string> format)
    {
        var vals = points.Where(p => p.Value.HasValue).Select(p => p.Value!.Value).ToList();
        if (vals.Count == 0)
        {
            return [];
        }

        double hi = vals.Max();
        double lo = Math.Min(0, vals.Min());
        if (hi <= lo)
        {
            hi = lo + 1;
        }

        return [(lo, format(lo)), ((lo + hi) / 2, format((lo + hi) / 2)), (hi, format(hi))];
    }

    private string Range(ProgressionBucket b) => progression!.GroupBy switch
    {
        ProgressionGroupBy.Day => b.Start.ToString("dd.MM.yyyy"),
        ProgressionGroupBy.Month => b.Start.ToString("MMM yyyy"),
        _ => $"{b.Start.ToString("dd.MM")}–{b.End.ToString("dd.MM.yyyy")}",
    };
}
