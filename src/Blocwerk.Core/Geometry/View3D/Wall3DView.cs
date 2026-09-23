// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Everything the 3D wall renderer (<c>wwwroot/js/wall3d.js</c>) draws, in the world frame of
/// <c>wall-geometry.json</c>: millimetres, z up, the climber standing on the −y side. Vectors are
/// <c>[x, y, z]</c> arrays so the payload serialises to plain JSON for JS interop.
/// </summary>
public sealed record Wall3DView
{
    public Guid WallId { get; init; }

    public string WallName { get; init; } = string.Empty;

    /// <summary>The boulder whose holds are highlighted, when one was asked for and found.</summary>
    public Guid? BoulderId { get; init; }

    public string? BoulderName { get; init; }

    public double MarkerSizeMm { get; init; }

    public IReadOnlyList<Wall3DFacet> Facets { get; init; } = [];

    public IReadOnlyList<Wall3DMarker> Markers { get; init; } = [];

    public IReadOnlyList<Wall3DHold> Holds { get; init; } = [];

    /// <summary>
    /// Live holds of the wall that could not be placed (no facet / plane coordinates yet, or a facet
    /// the active model does not know). Reported so the page can say "N holds not measured yet".
    /// </summary>
    public int UnplacedHoldCount { get; init; }

    /// <summary>
    /// Physical holds drawn once although several overlapping panels each store a copy of them (each
    /// such <see cref="Wall3DHold"/> lists the folded copies in <see cref="Wall3DHold.DuplicateIds"/>).
    /// </summary>
    public int MultiPanelHoldCount { get; init; }

    /// <summary>
    /// Per-facet rectified photo textures. Empty until the rectification pipeline produces them; the
    /// renderer maps each onto its facet by <see cref="Wall3DTexture.Bounds"/>.
    /// </summary>
    public IReadOnlyList<Wall3DTexture> Textures { get; init; } = [];

    /// <summary>A photo-real Gaussian-splat scene (<c>.spz</c>) of the wall. Null until one exists.</summary>
    public string? SplatUrl { get; init; }

    /// <summary>
    /// The same scene pruned for phones and low-memory devices (same frame, same
    /// <see cref="SplatMatrix"/>); null when the full scene is small enough or has no mobile copy yet.
    /// </summary>
    public string? SplatMobileUrl { get; init; }

    /// <summary>
    /// The scene's levels of detail, smallest first, the full scene last (<see cref="Blocwerk.Core.Capture.SplatLodLadder"/>;
    /// same frame and <see cref="SplatMatrix"/>). The view starts on the first and steps up while the
    /// device keeps up. Empty without a splat; just the full scene when it has no ladder yet.
    /// </summary>
    public IReadOnlyList<Wall3DSplatLevel> SplatLevels { get; init; } = [];

    /// <summary>
    /// Column-major 4×4 (three.js <c>Matrix4.fromArray</c>) from the splat's own coordinates into this
    /// view's world frame (wall-geometry mm, z up). Set whenever <see cref="SplatUrl"/> is.
    /// </summary>
    public IReadOnlyList<double>? SplatMatrix { get; init; }
}

/// <summary>One planar facet of the wall, with its drawable quad.</summary>
/// <param name="Id">Facet id from the geometry document ("0", "5a", …).</param>
/// <param name="Segment">Segment index the facet belongs to.</param>
/// <param name="Name">Human label (segment name, plus the facet id on folded segments).</param>
/// <param name="Origin">Facet origin in world mm.</param>
/// <param name="U">Unit vector across the surface (right).</param>
/// <param name="V">Unit vector up the surface.</param>
/// <param name="Normal">Unit normal, out of the wall toward the climber.</param>
/// <param name="Corners">Quad corners in world mm: (aMin,bMin), (aMax,bMin), (aMax,bMax), (aMin,bMax).</param>
/// <param name="Extent">The plane rectangle the quad spans.</param>
/// <param name="AngleDeg">Tilt from vertical in degrees as the solve measured it (declared as a fallback); null when unknown.</param>
public sealed record Wall3DFacet(
    string Id,
    int Segment,
    string Name,
    double[] Origin,
    double[] U,
    double[] V,
    double[] Normal,
    IReadOnlyList<double[]> Corners,
    PlaneRectMm Extent,
    double? AngleDeg);

/// <summary>A placed ArUco marker, corners TL, TR, BR, BL in world mm.</summary>
public sealed record Wall3DMarker(int Id, string FacetId, IReadOnlyList<double[]> Corners, bool Synthetic);

/// <summary>A hold placed on its facet.</summary>
/// <param name="Id">Hold id.</param>
/// <param name="FacetId">Facet the hold sits on (its frame orients the disc).</param>
/// <param name="Position">Centre in world mm, lifted slightly off the facet along its normal.</param>
/// <param name="PlaneA">Centre along the facet's u axis, mm.</param>
/// <param name="PlaneB">Centre along the facet's v axis, mm.</param>
/// <param name="WidthMm">Size along u (measured, or the default when <paramref name="SizeMeasured"/> is false).</param>
/// <param name="HeightMm">Size along v.</param>
/// <param name="SizeMeasured">False when the size is a stand-in default.</param>
/// <param name="Color">Hold colour key (HoldPalette), null when uncoloured.</param>
/// <param name="ColorName">Display name of the colour.</param>
/// <param name="Hex">Render colour, as the rest of the app draws it.</param>
/// <param name="IsFoot">True for foot-category holds.</param>
/// <param name="UsageCount">Number of live (non-archived) boulders that use the hold.</param>
/// <param name="Role">Role in the highlighted boulder, or null when not part of it / no boulder.</param>
/// <param name="Shape">The hold's outline on the facet, mm relative to (PlaneA, PlaneB); null only from old callers.</param>
/// <param name="DuplicateIds">
/// Other panels' copies of this physical hold, folded into this one (not drawn themselves); null when
/// the hold is seen on one panel only. A tap or a boulder on any of them resolves to this hold.
/// </param>
public sealed record Wall3DHold(
    Guid Id,
    string FacetId,
    double[] Position,
    double PlaneA,
    double PlaneB,
    double WidthMm,
    double HeightMm,
    bool SizeMeasured,
    string? Color,
    string ColorName,
    string Hex,
    bool IsFoot,
    int UsageCount,
    Wall3DHoldRole? Role,
    Wall3DHoldShape? Shape = null,
    IReadOnlyList<Guid>? DuplicateIds = null);

/// <summary>A hold's part in the highlighted boulder. Serialised by name for the renderer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Wall3DHoldRole>))]
public enum Wall3DHoldRole
{
    /// <summary>A start hold.</summary>
    Start,

    /// <summary>A top (finish) hold.</summary>
    Top,

    /// <summary>Any other hand hold of the boulder.</summary>
    Hand,

    /// <summary>A foot-only hold of the boulder.</summary>
    Foot,

    /// <summary>Not in the boulder's list but a foothold through its "feet of one colour" rule.</summary>
    ColorFoot,
}

/// <summary>
/// A rectified image for one facet, covering <paramref name="Bounds"/> of its plane. <paramref name="MaskUrl"/>
/// is its photo-coverage mask (the texture's alpha: uncovered parts show the plain facet), null for
/// textures made before masks existed.
/// </summary>
public sealed record Wall3DTexture(string FacetId, string Url, PlaneRectMm Bounds, string? MaskUrl = null);

/// <summary>One level of detail of the photo-real scene.</summary>
/// <param name="Url">Byte route of the level's <c>.spz</c>.</param>
/// <param name="Splats">Its splat count; 0 when unknown (a full scene stored before counts were kept).</param>
/// <param name="SizeBytes">Its download size.</param>
public sealed record Wall3DSplatLevel(string Url, int Splats, long SizeBytes);
