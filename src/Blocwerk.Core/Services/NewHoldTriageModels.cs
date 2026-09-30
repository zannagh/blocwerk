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

/// <summary>The triage's outcome for the session: default discards, and the kept new holds the 3D model also sees.</summary>
/// <summary>A staged photo matched to the model: its registrations, size and EXIF focal length (px).</summary>
internal sealed record StagedRegistration(IReadOnlyList<FacetRegistration> Registrations, int Width, int Height, double? FocalPx)
{
    public static StagedRegistration None { get; } = new([], 0, 0, null);
}

internal sealed record NewHoldTriageOutcome(Dictionary<Guid, NewHoldDiscardReason> Discards, List<Guid> SeenIn3D)
{
    public Guid? Evidence3DModelId { get; set; }
}
