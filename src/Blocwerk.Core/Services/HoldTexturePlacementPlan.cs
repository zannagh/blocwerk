using System.Text.Json;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Services;

/// <summary>A hold's planned placement.</summary>
/// <param name="Hold">The hold as it was read (untracked).</param>
/// <param name="Fit">Where it goes.</param>
/// <param name="Metric">Its size through that placement, or null when unmeasurable.</param>
internal sealed record PlannedPlacement(Hold Hold, HoldPlaneFit Fit, HoldMetric? Metric);

/// <summary>One panel photo's plan: its summary so far and the placements to write.</summary>
/// <param name="Summary">Counts before writing (placed = planned).</param>
/// <param name="Placements">The placements.</param>
internal sealed record PanelPlan(HoldPlacementPanelSummary Summary, List<PlannedPlacement> Placements);

/// <summary>JSON of <see cref="HoldPlacementRun.PanelsJson"/>.</summary>
internal static class PanelSummaries
{
    public static string ToJson(IEnumerable<HoldPlacementPanelSummary> panels) => JsonSerializer.Serialize(panels);

    public static IReadOnlyList<HoldPlacementPanelSummary> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<HoldPlacementPanelSummary>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The reported form of a registration.</summary>
    public static FacetRegistrationSummary Of(FacetRegistration r) =>
        new(r.FacetId, r.Accepted, r.Matches, r.Inliers, Math.Round(r.Coverage, 3), r.RmsMm is { } rms ? Math.Round(rms, 2) : null, r.Reason);
}
