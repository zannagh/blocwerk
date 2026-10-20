// <copyright file="HybridZoomInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>The panel a close-up is for, as stamped when read.</summary>
/// <param name="Id">The panel.</param>
/// <param name="Generation">Its photo generation.</param>
/// <param name="Revision">Its in-place photo revision (a crop).</param>
/// <param name="CreatedAt">When its photo was added.</param>
internal sealed record ZoomPanel(Guid Id, int Generation, int Revision, DateTimeOffset CreatedAt);

/// <summary>A capture photo on disk, by its camera name.</summary>
/// <param name="StoredPath">Stored name in the capture file store.</param>
/// <param name="Width">Raw pixel width.</param>
/// <param name="Height">Raw pixel height.</param>
/// <param name="Sharpness">Sharpness score, when measured.</param>
internal sealed record ZoomSourcePhoto(string StoredPath, int Width, int Height, double? Sharpness);

/// <summary>An active model's frames and cameras, parsed once per model (its JSON never changes).</summary>
/// <param name="Document">The geometry document.</param>
/// <param name="Frames">Facet frames by id.</param>
/// <param name="Extents">Facet extents by id.</param>
/// <param name="Cameras">Solved capture cameras by image name.</param>
/// <param name="Facets">The facets with an extent, to cast close-up rays onto (<see cref="CloseUpFacetMask"/>).</param>
internal sealed record ZoomModel(
    WallGeometryDocument Document,
    IReadOnlyDictionary<string, FacetFrame> Frames,
    IReadOnlyDictionary<string, PlaneRectMm> Extents,
    IReadOnlyDictionary<string, SolvedCamera> Cameras,
    IReadOnlyList<CastFacet>? Facets = null);

/// <summary>Everything one close-up is planned from.</summary>
/// <param name="Model">The active model.</param>
/// <param name="Photos">The retained capture photos by camera name.</param>
/// <param name="Projector">Panel photo → facet plane mappings.</param>
/// <param name="ProbeGeneration">The generation the panel's placed holds carry (the projector's photo key).</param>
/// <param name="PanelInfo">The panel photo's raw size.</param>
/// <param name="Volumes">The model's visible volumes by facet.</param>
internal sealed record ZoomInputs(
    ZoomModel Model,
    IReadOnlyDictionary<string, ZoomSourcePhoto> Photos,
    HoldPlaneProjector Projector,
    int ProbeGeneration,
    PanelPhotoInfo PanelInfo,
    IReadOnlyDictionary<string, List<VolumeOutline>> Volumes)
{
    /// <summary>A hold-shaped probe at a panel point, for <see cref="HoldPlaneProjector.Place"/>.</summary>
    /// <param name="panelId">The panel.</param>
    /// <param name="x">Normalised x.</param>
    /// <param name="y">Normalised y.</param>
    /// <returns>The probe.</returns>
    public Hold Probe(Guid panelId, double x, double y) => new() { WallPanelId = panelId, Generation = ProbeGeneration, X = x, Y = y };
}
