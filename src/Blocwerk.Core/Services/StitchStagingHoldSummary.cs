namespace Blocwerk.Core.Services;

/// <summary>
/// How the pipeline's carryover classifications landed in the staged generation.
/// <paramref name="Unreported"/> counts live holds the carryover said nothing about at all; they are
/// carried forward untouched and flagged for review rather than dropped.
/// </summary>
public sealed record StitchStagingHoldSummary(int CarriedOver, int Missing, int New, int Unreported)
{
    /// <summary>Total holds written into the staged generation.</summary>
    public int Total => CarriedOver + Missing + New + Unreported;

    /// <summary>Carried-over holds whose match was too weak to trust, so they were flagged.</summary>
    public int CarriedOverNeedingReview { get; init; }

    /// <summary>Holds the admin has to look at before confirming.</summary>
    public int NeedsReview => CarriedOverNeedingReview + Missing + New + Unreported;

    /// <summary>
    /// The pipeline's standing caveat about the carryover's accuracy, empty when it did not run.
    /// While it is set the staged result must not be promoted to live unattended.
    /// </summary>
    public string? Blocker { get; init; }
}
