// <copyright file="NewHoldTriageModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>One staged panel's triage: its default discards, its photo size and the unpaired detections it kept (RAW pixels).</summary>
internal sealed record PanelTriage(
    Dictionary<Guid, NewHoldDiscardReason> Discards,
    (int Width, int Height) Size,
    IReadOnlyList<(double X, double Y)> KeptNew);

/// <summary>The triaged staged centre, as a neighbour's triage needs it: its holds by id, photo size and kept new holds.</summary>
internal sealed record TriagedCentre(
    IReadOnlyDictionary<Guid, Hold> Holds,
    (int Width, int Height) Size,
    IReadOnlyList<(double X, double Y)> KeptNew);

/// <summary>The active model as the triage uses it; its textures are read only when a photo must be registered.</summary>
internal sealed record Evidence3DModel(
    Guid Id,
    string Json,
    DateTimeOffset CreatedAt,
    IReadOnlyList<FacetSpot> KnownHolds,
    IReadOnlyList<FacetSpot> SeenIn3D,
    IReadOnlyDictionary<string, ModelFacet> Facets)
{
    public List<RegistrationTexture>? Textures { get; set; }
}

/// <summary>A staged photo matched to the model: its registrations, size and EXIF focal length (px).</summary>
internal sealed record StagedRegistration(IReadOnlyList<FacetRegistration> Registrations, int Width, int Height, double? FocalPx)
{
    public static StagedRegistration None { get; } = new([], 0, 0, null);
}

/// <summary>One staged photo's usable 3D evidence, and whether the model's texture shows the wall as photographed.</summary>
/// <param name="Evidence">The registration and what the model knows.</param>
/// <param name="FromThisVisit">The model was built after the photo was staged.</param>
internal sealed record PanelEvidence3D(Panel3DEvidence Evidence, bool FromThisVisit);

/// <summary>
/// The triage's outcome for the session: default discards, the kept new holds the 3D model also sees, and the evidence
/// the confirm screen asks about (old holds possibly removed, discarded detections the 3D model sees as holds).
/// </summary>
internal sealed record NewHoldTriageOutcome(Dictionary<Guid, NewHoldDiscardReason> Discards, List<Guid> SeenIn3D)
{
    public Guid? Evidence3DModelId { get; set; }

    public List<PossiblyRemovedHold> PossiblyRemoved { get; } = [];

    /// <summary>When the removal checks of this run run out of time (set by the first one).</summary>
    public DateTimeOffset? RemovalDeadline { get; set; }

    public List<ConflictingNewHold> ConflictingNew { get; } = [];
}

/// <summary>What the removal check needs besides the panels: the old holds per position, which were not found again, and where they should be.</summary>
/// <param name="OldByPosition">The carried old holds per grid position.</param>
/// <param name="NotFoundAgain">The old holds the matcher found no twin for.</param>
/// <param name="Warp">The matcher's warp-predicted position of each old hold on its staged photo.</param>
internal sealed record RemovalInputs(
    IReadOnlyDictionary<(int Col, int Row), List<Hold>> OldByPosition,
    IReadOnlySet<Guid> NotFoundAgain,
    IReadOnlyDictionary<Guid, HoldPositionNorm> Warp);

/// <summary>A staged panel the triage walks.</summary>
internal sealed record StagedPanelRef(Guid Id, int Col, int Row);

/// <summary>What every panel's triage shares.</summary>
internal sealed record TriageContext(
    Wall Wall,
    int StagedGen,
    IReadOnlyList<CarryoverProposal> Carryover,
    IReadOnlyDictionary<Guid, Hold> OldById,
    IReadOnlyDictionary<Guid, byte[]> OldPanelPhotosById,
    IReadOnlyList<NeighbourOverlap> Neighbours,
    Evidence3DModel? Model3D,
    RemovalInputs Removals);

/// <summary>One staged panel as the removal check sees it.</summary>
/// <param name="PanelId">The staged panel.</param>
/// <param name="Staged">Its staged detections.</param>
/// <param name="Twins">Its matched old/staged pairs.</param>
/// <param name="Candidates">Its old holds not found again.</param>
/// <param name="OldPhoto">The old photo of the panel, or null.</param>
internal sealed record RemovalScope(
    Guid PanelId,
    IReadOnlyList<Hold> Staged,
    IReadOnlyList<(Hold Old, Hold New)> Twins,
    IReadOnlyList<Hold> Candidates,
    byte[]? OldPhoto);
