// <copyright file="WallMarkersApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>PUT /api/walls/{wallId}/markers</c>, the marker settings' "Save".</summary>
/// <param name="Enabled">Whether the wall carries printed markers.</param>
/// <param name="MarkerSizeMm">The black square's side in mm, (0, 1000]; null keeps the stored size.</param>
public sealed record WallMarkerSettingsRequest(bool Enabled, double? MarkerSizeMm = null);

/// <summary>A wall's marker preparation: the declaration and the marker plan's revisions.</summary>
/// <param name="Enabled">Whether markers are switched on for the wall.</param>
/// <param name="MarkerSizeMm">The stored marker size in mm, or null.</param>
/// <param name="CurrentRevision">The current plan revision, or null without a plan.</param>
/// <param name="OnWallRevision">The newest revision known to be on the wall (marked effective or measured), or null.</param>
/// <param name="Revisions">Every revision, newest first.</param>
public sealed record WallMarkerStateResponse(
    bool Enabled,
    double? MarkerSizeMm,
    int? CurrentRevision,
    int? OnWallRevision,
    IReadOnlyList<MarkerPlanRevisionResponse> Revisions);

/// <summary>The outcome of uploading a marker plan.</summary>
/// <param name="Saved">True when it is now the wall's current plan.</param>
/// <param name="Revision">Its revision number once saved.</param>
/// <param name="Unchanged">True when it was exactly the current plan (no new revision).</param>
/// <param name="Issues">What the validator found; errors block saving (then 422).</param>
public sealed record WallMarkerPlanSaveResponse(bool Saved, int? Revision, bool Unchanged, IReadOnlyList<PlanIssue> Issues);
