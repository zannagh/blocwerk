// <copyright file="WallMarkerLayout.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// What a wall's printed markers mean: which ids exist, which segment each sits on, how big it was
/// printed, where it was planned, and what the owner said about each surface. Built from the wall's
/// marker plan when it has one (<see cref="FromPlan"/>), else from the legacy <c>segment*6+role</c>
/// convention with one size per wall (<see cref="Legacy"/>). Everything that used to do id arithmetic
/// asks this instead; resolve one with <see cref="WallMarkerLayoutResolver"/>.
/// </summary>
public sealed class WallMarkerLayout
{
    /// <summary>The legacy scheme's roles per segment (<c>id = segment*6 + role</c>).</summary>
    public const int LegacyRolesPerSegment = 6;

    /// <summary>Highest legacy id (6 segments × 6 roles).</summary>
    public const int LegacyMaxMarkerId = 35;

    /// <summary>
    /// A planned surface within this many degrees of plumb is a gravity reference — the same tolerance
    /// under which every label calls it "vertical" (<see cref="SurfaceAngle.VerticalToleranceDeg"/>).
    /// </summary>
    public const double VerticalToleranceDeg = SurfaceAngle.VerticalToleranceDeg;

    /// <summary>The solve request's id scheme without a plan.</summary>
    public const string LegacyIdScheme = "segment*6+role";

    /// <summary>The solve request's id scheme with a plan (segments come from <c>markerSegments</c>).</summary>
    public const string PlanIdScheme = "plan";

    private static readonly string[] LegacyRoles = ["TL", "TR", "BR", "BL", "H", "V"];

    private WallMarkerLayout(
        MarkerPlan? plan,
        double? defaultSizeMm,
        IReadOnlyDictionary<int, LayoutMarker> markers,
        IReadOnlyList<LayoutSegment> segments,
        IReadOnlyList<LayoutSharedEdge> sharedEdges)
    {
        Plan = plan;
        DefaultSizeMm = defaultSizeMm;
        Markers = markers;
        Segments = segments;
        SharedEdges = sharedEdges;
        AllowedIds = plan is null ? MarkerDetectionOptions.DefaultAllowedIds : markers.Keys.ToHashSet();
    }

    /// <summary>The plan behind this layout; null for the legacy convention.</summary>
    public MarkerPlan? Plan { get; }

    /// <summary>True when the layout comes from a marker plan.</summary>
    public bool IsFromPlan => Plan is not null;

    /// <summary>
    /// The size most markers have (the solve request's <c>markerSizeMm</c>). Legacy: the wall's stated
    /// size, null when it never stated one.
    /// </summary>
    public double? DefaultSizeMm { get; }

    /// <summary>Ids that may appear on the wall; anything else is a false positive.</summary>
    public IReadOnlySet<int> AllowedIds { get; }

    /// <summary>Every known marker by id (legacy: ids 0..35).</summary>
    public IReadOnlyDictionary<int, LayoutMarker> Markers { get; }

    /// <summary>The planned surfaces; empty for the legacy convention (declarations come from the admin).</summary>
    public IReadOnlyList<LayoutSegment> Segments { get; }

    /// <summary>Seams between segments, from the plan's net attachments; empty for the legacy convention.</summary>
    public IReadOnlyList<LayoutSharedEdge> SharedEdges { get; }

    /// <summary>The highest id a declaration (e.g. a level pair) may name.</summary>
    public int MaxMarkerId => IsFromPlan ? (AllowedIds.Count == 0 ? 0 : AllowedIds.Max()) : LegacyMaxMarkerId;

    /// <summary>The id scheme to announce to the solver.</summary>
    public string IdScheme => IsFromPlan ? PlanIdScheme : LegacyIdScheme;

    /// <summary>Detection options: the plan's ids, or exactly the legacy defaults.</summary>
    public MarkerDetectionOptions DetectionOptions => IsFromPlan
        ? MarkerDetectionOptions.Default with { AllowedIds = AllowedIds }
        : MarkerDetectionOptions.Default;

    /// <summary>
    /// The segment a marker sits on. Legacy: <c>id / 6</c> for ANY id (as the old arithmetic did);
    /// plan: its planned segment, or null for an id the plan does not list.
    /// </summary>
    public int? SegmentOf(int markerId) => IsFromPlan
        ? Markers.TryGetValue(markerId, out var marker) ? marker.Segment : null
        : markerId / LegacyRolesPerSegment;

    /// <summary>The printed size of a marker; falls back to <see cref="DefaultSizeMm"/>.</summary>
    public double? SizeOf(int markerId) =>
        Markers.TryGetValue(markerId, out var marker) ? marker.SizeMm ?? DefaultSizeMm : DefaultSizeMm;

    /// <summary>The ids on one segment, ascending (legacy: its six role ids).</summary>
    public IReadOnlyList<int> IdsOn(int segment) => IsFromPlan
        ? Markers.Values.Where(m => m.Segment == segment).Select(m => m.Id).Order().ToList()
        : Enumerable.Range(segment * LegacyRolesPerSegment, LegacyRolesPerSegment).ToList();

    /// <summary>The planned segment with this index, or null (always null for the legacy convention).</summary>
    public LayoutSegment? FindSegment(int index) => Segments.FirstOrDefault(s => s.Index == index);

    /// <summary>The legacy convention: ids 0..35, <c>id / 6</c> segments, one size for the whole wall.</summary>
    public static WallMarkerLayout Legacy(double? markerSizeMm)
    {
        var markers = MarkerDetectionOptions.DefaultAllowedIds
            .Order()
            .ToDictionary(
                id => id,
                id => new LayoutMarker(id, id / LegacyRolesPerSegment, markerSizeMm, LegacyRoles[id % LegacyRolesPerSegment], null, null));
        return new WallMarkerLayout(null, markerSizeMm, markers, [], []);
    }

    /// <summary>The layout a marker plan describes.</summary>
    public static WallMarkerLayout FromPlan(MarkerPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var markers = new Dictionary<int, LayoutMarker>();
        foreach (var m in plan.Markers)
        {
            markers.TryAdd(m.Id, new LayoutMarker(m.Id, m.Segment, m.SizeMm, RoleName(m.Role), m.XMm, m.YMm));
        }

        var segments = plan.Segments
            .Select(s => new LayoutSegment(
                s.Index,
                s.Name,
                s.OverhangDeg,
                s.YawDeg,
                SurfaceAngle.IsVertical(s.OverhangDeg),
                SegmentOutline.Vertices(s).Select(v => new[] { v.X, v.Y }).ToList()))
            .ToList();
        var seams = plan.Segments
            .Where(s => s.AttachedTo is not null)
            .Select(s => new LayoutSharedEdge(
                s.Index, s.AttachedTo!.OwnEdge, s.AttachedTo.ParentIndex, s.AttachedTo.ParentEdge, s.AttachedTo.OffsetMm))
            .ToList();
        return new WallMarkerLayout(plan, MostCommonSize(plan), markers, segments, seams);
    }

    private static string RoleName(MarkerRole role) => role == MarkerRole.Corner ? "corner" : "filler";

    /// <summary>The size most markers share (ties: the larger), so the fewest need a per-id override.</summary>
    private static double? MostCommonSize(MarkerPlan plan) => plan.Markers.Count == 0
        ? null
        : plan.Markers
            .GroupBy(m => m.SizeMm)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .First().Key;
}
