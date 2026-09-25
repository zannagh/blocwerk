namespace Blocwerk.Core.Services;

/// <summary>What started a placement run (stored on the run).</summary>
public static class HoldPlacementTrigger
{
    /// <summary>The wall-settings button.</summary>
    public const string Admin = "admin";

    /// <summary>The machine API.</summary>
    public const string Api = "api";

    /// <summary>Automatically, after a capture's model went live.</summary>
    public const string Capture = "capture";
}

/// <summary>Whether the action can run on a wall, and its latest run.</summary>
/// <param name="Enabled">False when this host cannot match photos to textures.</param>
/// <param name="HasTextures">Whether the wall's active model has facet textures to register onto.</param>
/// <param name="LatestRun">The newest run on the wall, or null.</param>
public sealed record HoldPlacementStatus(bool Enabled, bool HasTextures, HoldPlacementRunInfo? LatestRun);

/// <summary>A run, as the admin UI and the API show it.</summary>
/// <param name="Id">Run id (revert it with this).</param>
/// <param name="CreatedAt">When it ran.</param>
/// <param name="Trigger">What started it (<see cref="HoldPlacementTrigger"/>).</param>
/// <param name="Placed">Holds that got a facet position.</param>
/// <param name="Skipped">Holds left alone (placed by other means, virtual, no panel photo).</param>
/// <param name="Failed">Holds in scope that no registered facet contained.</param>
/// <param name="Panels">Per panel photo.</param>
/// <param name="RevertedAt">When it was reverted, or null.</param>
public sealed record HoldPlacementRunInfo(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Trigger,
    int Placed,
    int Skipped,
    int Failed,
    IReadOnlyList<HoldPlacementPanelSummary> Panels,
    DateTimeOffset? RevertedAt);

/// <summary>One panel photo of a run.</summary>
/// <param name="PanelId">The panel row.</param>
/// <param name="Col">Panel column.</param>
/// <param name="Row">Panel row.</param>
/// <param name="Placed">Holds placed.</param>
/// <param name="Skipped">Holds left alone.</param>
/// <param name="Failed">Holds no registered facet contained (all of them when the photo registered nowhere).</param>
/// <param name="Facets">Each facet texture's registration evidence.</param>
/// <param name="Problem">Why the photo could not be used at all, or null.</param>
/// <param name="Carried">Of <paramref name="Placed"/>, the holds the photo could not be registered for that kept their previous placement, carried over from the earlier model.</param>
public sealed record HoldPlacementPanelSummary(
    Guid PanelId,
    int Col,
    int Row,
    int Placed,
    int Skipped,
    int Failed,
    IReadOnlyList<FacetRegistrationSummary> Facets,
    string? Problem,
    int Carried = 0)
{
    /// <summary>Gets the short name admins know the panel by, e.g. "c0" or "c1 r1".</summary>
    public string Label => Row == 0 ? $"c{Col}" : $"c{Col} r{Row}";
}

/// <summary>One photo × facet registration, as reported.</summary>
/// <param name="FacetId">The facet.</param>
/// <param name="Accepted">Whether it placed holds.</param>
/// <param name="Matches">Correspondences found.</param>
/// <param name="Inliers">Correspondences the fit explains.</param>
/// <param name="Coverage">Share of the facet in view spanned by the inliers.</param>
/// <param name="RmsMm">Inlier reprojection RMS on the plane, mm.</param>
/// <param name="Reason">Why it was refused, or null.</param>
public sealed record FacetRegistrationSummary(
    string FacetId, bool Accepted, int Matches, int Inliers, double Coverage, double? RmsMm, string? Reason);

/// <summary>What a run wrote.</summary>
/// <param name="RunId">The recorded run.</param>
/// <param name="Placed">Holds placed.</param>
/// <param name="Skipped">Holds left alone.</param>
/// <param name="Failed">Holds that could not be placed.</param>
/// <param name="Panels">Per panel photo.</param>
public sealed record HoldPlacementResult(
    Guid RunId, int Placed, int Skipped, int Failed, IReadOnlyList<HoldPlacementPanelSummary> Panels);

/// <summary>What a revert restored.</summary>
/// <param name="Reverted">Holds restored to what they were before the run.</param>
/// <param name="SkippedEdited">Holds moved or re-placed since the run, left as they are.</param>
/// <param name="Missing">Holds deleted since the run.</param>
public sealed record HoldPlacementRevertResult(int Reverted, IReadOnlyList<Guid> SkippedEdited, int Missing);
