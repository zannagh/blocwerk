// <copyright file="IMarkerPlanService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The marker planner: store a wall's plan, suggest a decodable marker layout, check it, draw it as an
/// unfolded net, and export it as JSON (to accompany a photo dump) and as a printable PDF.
/// </summary>
public interface IMarkerPlanService
{
    /// <summary>The wall's saved plan, or null. Visible to anyone who can see the wall.</summary>
    Task<MarkerPlan?> GetPlanAsync(Guid wallId);

    /// <summary>
    /// Validates and saves the wall's plan as its next revision (wall admins only, never from a kiosk).
    /// Saving exactly the current plan again adds no revision.
    /// </summary>
    Task<MarkerPlanSaveResult> SavePlanAsync(Guid wallId, MarkerPlan plan);

    /// <summary>The wall's plan revisions, newest first. Wall admins only, never from a kiosk.</summary>
    Task<IReadOnlyList<MarkerPlanRevisionInfo>> GetRevisionsAsync(Guid wallId);

    /// <summary>
    /// Records that <paramref name="revision"/>'s markers were put up on the wall at <paramref name="effectiveFrom"/>
    /// ("Markers swapped on the wall"); null clears it (back to "planned, not yet on the wall"). A prior for which
    /// revision a photo shows — the photo's markers decide first. False when the revision does not exist.
    /// Wall admins only, never from a kiosk.
    /// </summary>
    Task<bool> SetRevisionEffectiveAsync(Guid wallId, int revision, DateTimeOffset? effectiveFrom);

    /// <summary>One stored revision of the wall's plan, or null. Wall admins only, never from a kiosk.</summary>
    Task<MarkerPlan?> GetRevisionAsync(Guid wallId, int revision);

    /// <summary>
    /// What changed between the markers the wall's ACTIVE model measured (its plan revision, or the legacy
    /// markers) and <paramref name="plan"/> (the plan being edited; null = the saved current plan). Null
    /// without an active model: nothing was captured yet. Wall admins only, never from a kiosk.
    /// </summary>
    Task<MarkerPlanChanges?> GetChangesSinceLastCaptureAsync(Guid wallId, MarkerPlan? plan = null);

    /// <summary>
    /// The markers the wall's ACTIVE model measured, as a plan reads them (its plan revision's markers, or
    /// the legacy markers), for comparing an edited plan live; null without an active model. Admins only.
    /// </summary>
    Task<MarkerCaptureBaseline?> GetCaptureBaselineAsync(Guid wallId);

    /// <summary>
    /// A starting plan built from the wall's ACTIVE measured geometry (<see cref="MarkerPlanFromGeometry"/>),
    /// photographed as <paramref name="photo"/>; null when the wall has no usable geometry. Wall admins
    /// only, never from a kiosk — the geometry itself is admin-only.
    /// </summary>
    Task<MarkerPlan?> BuildFromMeasuredGeometryAsync(Guid wallId, PhotoSetup photo);

    /// <summary>Returns <paramref name="plan"/> with its markers REPLACED by a suggested layout.</summary>
    MarkerPlan GenerateMarkers(MarkerPlan plan, MarkerGenerationOptions options);

    /// <summary>Checks the plan: geometry, attachments, ids, sizes vs. distance, coverage.</summary>
    IReadOnlyList<PlanIssue> Validate(MarkerPlan plan);

    /// <summary>Lays the segments out as an unfolded net in one drawing frame.</summary>
    NetGeometry ComputeNet(MarkerPlan plan);

    /// <summary>Serializes the plan to its versioned JSON form.</summary>
    string ToJson(MarkerPlan plan);

    /// <summary>Parses plan JSON; null plus readable errors when it is not a valid plan.</summary>
    MarkerPlan? FromJson(string json, out IReadOnlyList<string> errors);

    /// <summary>
    /// Renders the printable PDF: placement map, instructions, every marker at true size — or, with
    /// <paramref name="printOnly"/>, only those markers (e.g. the ones changed since the last capture).
    /// </summary>
    byte[] RenderPdf(MarkerPlan plan, string wallName, IReadOnlySet<int>? printOnly = null);
}

/// <summary>The outcome of saving a plan.</summary>
/// <param name="Saved">True when the plan was stored (or already was the current plan).</param>
/// <param name="Issues">Everything <see cref="IMarkerPlanService.Validate"/> found (errors block saving).</param>
/// <param name="Revision">The plan's revision number once saved.</param>
/// <param name="Unchanged">True when it was exactly the current plan, so no revision was added.</param>
public sealed record MarkerPlanSaveResult(bool Saved, IReadOnlyList<PlanIssue> Issues, int? Revision = null, bool Unchanged = false);
