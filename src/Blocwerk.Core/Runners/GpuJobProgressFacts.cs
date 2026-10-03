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
/// <param name="Step">The reported step (null: none).</param>
/// <param name="TotalSteps">The reported total (null: keep the stored one).</param>
/// <param name="Anchor">The step-rate anchor step.</param>
/// <param name="AnchorAt">When the anchor was reported.</param>
/// <param name="Loss">The trainer's loss, when the detail carries one (null: keep the stored one).</param>
/// <param name="Splats">The trainer's splat count, when the detail carries one (null: keep the stored one).</param>
internal sealed partial record GpuJobProgressFacts(int? Step, int? TotalSteps, int? Anchor, DateTimeOffset? AnchorAt, double? Loss, int? Splats)
{
    /// <summary>
    /// The facts of <paramref name="report"/> for <paramref name="job"/> at <paramref name="now"/>. The anchor is the first
    /// step of the current claim: it moves when there is none yet, when it predates the claim (a new claim, maybe resuming a
    /// checkpoint) or when the reported step went backwards (the trainer started over).
    /// </summary>
    public static GpuJobProgressFacts From(GpuJob job, RunnerProgress report, DateTimeOffset now)
    {
        var step = report.Step is >= 0 ? report.Step : null;
        var total = report.TotalSteps is > 0 ? report.TotalSteps : null;
        var (anchor, anchorAt) = (job.StepAnchor, job.StepAnchorAt);
        if (step is { } s && (anchorAt is null || anchorAt < job.ClaimedAt || anchor > s))
        {
            (anchor, anchorAt) = (s, now);
        }

        var detail = report.Detail ?? string.Empty;
        return new GpuJobProgressFacts(step ?? job.Step, total, anchor, anchorAt, ParseLoss(detail), ParseSplats(detail));
    }

    private static double? ParseLoss(string detail) =>
        LossPattern().Match(detail) is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var loss) && double.IsFinite(loss)
            ? loss
            : null;

    private static int? ParseSplats(string detail) =>
        SplatsPattern().Match(detail) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var splats)
            ? splats
            : null;

    [GeneratedRegex(@"\bloss[ =:]+([-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex LossPattern();

    [GeneratedRegex(@"\bsplats[ =:]+([0-9]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SplatsPattern();
}
