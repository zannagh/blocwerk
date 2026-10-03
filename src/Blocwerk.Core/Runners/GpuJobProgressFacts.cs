// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.RegularExpressions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The numbers of one progress report as they are stored on the job (<see cref="GpuJob.Step"/>, the step-rate anchor, the
/// trainer's loss and splat count), so the progress API derives rates and remaining time from facts, not from the stage text.
/// </summary>
/// <param name="Step">The step to store (the reported one, else the claim's last one; null on a new claim without one).</param>
/// <param name="TotalSteps">The reported total (null: keep the stored one).</param>
/// <param name="Anchor">The step-rate anchor step.</param>
/// <param name="AnchorAt">When the anchor was reported.</param>
/// <param name="Loss">The loss to store: the reported one, else the claim's last one (null on a new claim).</param>
/// <param name="Splats">The splat count to store: the reported one, else the claim's last one (null on a new claim).</param>
internal sealed partial record GpuJobProgressFacts(int? Step, int? TotalSteps, int? Anchor, DateTimeOffset? AnchorAt, double? Loss, int? Splats)
{
    /// <summary>
    /// The facts of <paramref name="report"/> for <paramref name="job"/> at <paramref name="now"/>. The anchor is the first
    /// step of the current claim: it moves when there is none yet, when it predates the claim (a new claim, maybe resuming a
    /// checkpoint) or when the reported step went backwards (the trainer started over). Until the current claim reported a
    /// step, the previous claim's step, loss and splat count are not carried over.
    /// </summary>
    public static GpuJobProgressFacts From(GpuJob job, RunnerProgress report, DateTimeOffset now)
    {
        var step = report.Step is >= 0 ? report.Step : null;
        var total = report.TotalSteps is > 0 ? report.TotalSteps : null;
        var (anchor, anchorAt) = (job.StepAnchor, job.StepAnchorAt);
        var newClaim = anchorAt is null || anchorAt < job.ClaimedAt;
        if (step is { } s && (newClaim || anchor > s))
        {
            (anchor, anchorAt) = (s, now);
        }

        var detail = report.Detail ?? string.Empty;
        var (loss, splats) = (ParseLoss(detail), ParseSplats(detail));
        return newClaim && step is null
            ? new GpuJobProgressFacts(null, total, anchor, anchorAt, loss, splats)
            : new GpuJobProgressFacts(
                step ?? job.Step, total, anchor, anchorAt, loss ?? (newClaim ? null : job.Loss), splats ?? (newClaim ? null : job.SplatCount));
    }

    /// <summary>The trainer's loss (<c>loss 0.1416</c>, invariant digits only); null for any other form.</summary>
    internal static double? ParseLoss(string detail) =>
        LossPattern().Match(detail) is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var loss)
        && double.IsFinite(loss)
            ? loss
            : null;

    /// <summary>The trainer's splat count (<c>splats 55966</c>, plain digits only); null for any other form.</summary>
    internal static int? ParseSplats(string detail) =>
        SplatsPattern().Match(detail) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var splats)
            ? splats
            : null;

    // The gsplat trainer prints "step 750/15000 splats 55966 loss 0.1416"; a number must end at whitespace, ';', ')' or the
    // end, so a decimal comma, a thousands separator or a suffix ("1.2M") reads as no value rather than a wrong one.
    [GeneratedRegex(@"\bloss[ =:]+([0-9]+(?:\.[0-9]+)?(?:[eE][-+]?[0-9]+)?)(?=$|[\s;)])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LossPattern();

    [GeneratedRegex(@"\bsplats[ =:]+([0-9]+)(?=$|[\s;)])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SplatsPattern();
}
