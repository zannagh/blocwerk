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
    IReadOnlyList<(double X, double Y)> KeptNew,
    IReadOnlyList<TriageSwap>? Swaps = null);

/// <summary>
/// The triaged staged centre, as a neighbour's triage needs it: its holds by id, photo size, kept new holds and the
/// holds it settled itself (carried, hand-placed or discarded by its triage), RAW pixels.
/// </summary>
internal sealed record TriagedCentre(
    IReadOnlyDictionary<Guid, Hold> Holds,
    (int Width, int Height) Size,
    IReadOnlyList<(double X, double Y)> KeptNew,
    IReadOnlyList<TriageSpot> Settled);

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

    /// <summary>Detections kept because their colour says another hold sits on a carried hold's spot, with the staged panel.</summary>
    public List<(TriageSwap Swap, Guid PanelId)> Swaps { get; } = [];
}

/// <summary>What the removal check needs besides the panels: the old holds per position, which were not found again, and where they should be.</summary>
/// <param name="OldByPosition">The carried old holds per grid position.</param>
/// <param name="NotFoundAgain">The old holds the matcher found no twin for.</param>
/// <param name="Warp">The matcher's warp-predicted position of each old hold on its staged photo.</param>
/// <param name="WarpShapes">The matcher's warp of each outlined old hold's outline onto its staged photo (absolute, normalized).</param>
/// <param name="NoLongerShown">
/// The old holds not found again whose spot no longer shows them, or shows another hold (the look check's verdict): a
/// detection on such a spot is what is there now, never a second outline of the old hold.
/// </param>
internal sealed record RemovalInputs(
    IReadOnlyDictionary<(int Col, int Row), List<Hold>> OldByPosition,
    IReadOnlySet<Guid> NotFoundAgain,
    IReadOnlyDictionary<Guid, HoldPositionNorm> Warp,
    IReadOnlyDictionary<Guid, IReadOnlyList<HoldPositionNorm>>? WarpShapes = null,
    IReadOnlySet<Guid>? NoLongerShown = null);

/// <summary>A staged panel the triage walks.</summary>
internal sealed record StagedPanelRef(Guid Id, int Col, int Row);

/// <summary>What every panel's triage shares.</summary>
internal sealed record TriageContext(
    Wall Wall,
    int StagedGen,
    IReadOnlyList<CarryoverProposal> Carryover,
    IReadOnlyDictionary<Guid, Hold> OldById,
    IReadOnlyDictionary<Guid, byte[]> OldPanelPhotosById,
    byte[]? OldCentrePhoto,
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

/// <summary>One staged panel as its new-hold triage sees it.</summary>
/// <param name="PanelId">The staged panel.</param>
/// <param name="Staged">Its staged detections.</param>
/// <param name="Twins">Its matched old/staged pairs.</param>
/// <param name="OldPhoto">The old photo of the panel, or null.</param>
/// <param name="OldPanelId">The old panel the photo belongs to (whose marker observations are read), or null.</param>
internal sealed record TriagedPanel(
    Guid PanelId,
    IReadOnlyList<Hold> Staged,
    IReadOnlyList<(Hold Old, Hold New)> Twins,
    byte[]? OldPhoto,
    Guid? OldPanelId);

/// <summary>An old hold no detection carries, where the matcher's warp puts it on the staged photo (normalized).</summary>
/// <param name="At">Its warped centre.</param>
/// <param name="Radius">Its radius (normalized to the old photo's longer side).</param>
/// <param name="Shape">Its warped outline (absolute vertices), or null for a circle.</param>
/// <param name="StillThere">
/// Whether its footprint counts as a carried hold for the inside-a-carried-hold rule: a physical hold the look check did
/// not find gone or replaced. A virtual hold, or one whose spot shows something else now, carries no second outline.
/// </param>
/// <param name="Id">The old hold.</param>
/// <param name="Lab">Its mean colour from its fingerprint, or null.</param>
internal sealed record WarpedOldHold(
    HoldPositionNorm At, double Radius, IReadOnlyList<HoldPositionNorm>? Shape, bool StillThere = true, Guid? Id = null, TriageLab? Lab = null);
