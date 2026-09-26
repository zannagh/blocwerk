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
/// <param name="Carried">Whether it is the previous placement carried over from an earlier model (<see cref="HoldMetric.TextureRegistrationCarried"/>).</param>
internal sealed record PlannedPlacement(Hold Hold, HoldPlaneFit Fit, HoldMetric? Metric, bool Carried = false);

/// <summary>One panel photo's plan: its summary so far and the placements to write.</summary>
/// <param name="Summary">Counts before writing (placed = planned).</param>
/// <param name="Placements">The placements.</param>
/// <param name="Registrations">The photo's registrations (the evidence a carried placement is checked against).</param>
/// <param name="Cleared">Holds whose previous placement the evidence contradicts: they lose it (counted as failed).</param>
internal sealed record PanelPlan(
    HoldPlacementPanelSummary Summary,
    List<PlannedPlacement> Placements,
    IReadOnlyList<FacetRegistration>? Registrations = null,
    IReadOnlyList<Hold>? Cleared = null);

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

/// <summary>The active model as a run uses it.</summary>
/// <param name="Id">The model.</param>
/// <param name="Textures">Its facet textures.</param>
/// <param name="Extents">Its facet extents by id.</param>
/// <param name="Frames">Its facet 3D frames by id.</param>
internal sealed record ActiveModel(
    Guid Id, List<RegistrationTexture> Textures, Dictionary<string, PlaneRectMm> Extents, Dictionary<string, FacetFrame> Frames);

/// <summary>A hold's previous placement moved onto the active model (<see cref="PlacementCarrier"/>).</summary>
/// <param name="FacetId">The facet.</param>
/// <param name="A">Plane a on the active model, mm.</param>
/// <param name="B">Plane b on the active model, mm.</param>
internal sealed record CarriedPosition(string FacetId, double A, double B);
