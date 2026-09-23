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

    /// <summary>Validates and saves the wall's plan (wall admins only, never from a kiosk).</summary>
    Task<MarkerPlanSaveResult> SavePlanAsync(Guid wallId, MarkerPlan plan);

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

    /// <summary>Renders the printable PDF: placement map, instructions, every marker at true size.</summary>
    byte[] RenderPdf(MarkerPlan plan, string wallName);
}

/// <summary>The outcome of saving a plan.</summary>
/// <param name="Saved">True when the plan was stored.</param>
/// <param name="Issues">Everything <see cref="IMarkerPlanService.Validate"/> found (errors block saving).</param>
public sealed record MarkerPlanSaveResult(bool Saved, IReadOnlyList<PlanIssue> Issues);
