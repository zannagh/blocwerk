// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Jobs;

/// <summary>
/// The remaining-time math of the progress API. Every method answers null when the facts do not support an answer: an
/// estimate is shown only when it is derived from something measured.
/// </summary>
public static class JobEta
{
    /// <summary>The least time a rate must be measured over before it is trusted.</summary>
    public static readonly TimeSpan MinRateWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Seconds left at the rate measured since <paramref name="anchorAt"/>: (<paramref name="total"/> − <paramref name="done"/>)
    /// ÷ ((<paramref name="done"/> − <paramref name="anchor"/>) ÷ elapsed). Null without progress since the anchor, over less than
    /// <see cref="MinRateWindow"/>, or with nothing left to measure against.
    /// </summary>
    public static double? FromRate(double? done, double? total, double? anchor, DateTimeOffset? anchorAt, DateTimeOffset now)
    {
        if (done is not { } d || total is not { } t || anchor is not { } a || anchorAt is not { } at || t <= 0 || d > t)
        {
            return null;
        }

        var elapsed = (now - at).TotalSeconds;
        var rate = Rate(d - a, elapsed);
        return rate is { } r ? Math.Round((t - d) / r, 1) : null;
    }

    /// <summary>Units per second over <paramref name="elapsedSeconds"/>, or null when not measurable yet.</summary>
    public static double? Rate(double progressed, double elapsedSeconds) =>
        progressed > 0 && elapsedSeconds >= MinRateWindow.TotalSeconds ? progressed / elapsedSeconds : null;

    /// <summary>
    /// Seconds left of a stage that usually takes <paramref name="median"/> and has run for <paramref name="elapsed"/>. Null
    /// without history, without a start, or once the stage overran its median (then nothing measured says how long is left).
    /// </summary>
    public static double? FromHistory(TimeSpan? median, TimeSpan? elapsed)
    {
        if (median is not { } m || elapsed is not { } e || e > m)
        {
            return null;
        }

        return Math.Round((m - (e < TimeSpan.Zero ? TimeSpan.Zero : e)).TotalSeconds, 1);
    }

    /// <summary>The median of <paramref name="durations"/>, or null when there are fewer than <paramref name="minSamples"/>.</summary>
    public static TimeSpan? Median(IEnumerable<TimeSpan> durations, int minSamples = 3)
    {
        var sorted = durations.Where(d => d >= TimeSpan.Zero).Order().ToList();
        if (sorted.Count < Math.Max(1, minSamples))
        {
            return null;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
    }

    /// <summary><paramref name="fraction"/> (0..1) as a percentage with one decimal, or null.</summary>
    public static double? Percent(double? fraction) =>
        fraction is { } f && double.IsFinite(f) ? Math.Round(Math.Clamp(f, 0, 1) * 100, 1) : null;
}
