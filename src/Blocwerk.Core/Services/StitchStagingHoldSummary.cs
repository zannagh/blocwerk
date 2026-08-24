namespace Blocwerk.Core.Services;

/// <summary>
/// How the sidecar's hold classifications landed in the staged generation.
/// <paramref name="Unreported"/> counts live holds the sidecar said nothing about at all; they are
/// carried forward untouched and flagged for review rather than dropped.
/// </summary>
public sealed record StitchStagingHoldSummary(int Matched, int Uncertain, int Missing, int Unreported)
{
    /// <summary>Total holds written into the staged generation.</summary>
    public int Total => Matched + Uncertain + Missing + Unreported;

    /// <summary>Holds the admin has to look at before confirming.</summary>
    public int NeedsReview => Uncertain + Missing + Unreported;
}
