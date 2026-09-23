using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// The outline of one hold on one photo.
/// </summary>
/// <param name="Polygon">The outline in normalized IMAGE coordinates (x as a fraction of the width, y as
/// a fraction of the height), ≤ 24 vertices, in contour order. For a
/// <see cref="HoldOutlineMethod.CircleFallback"/> this is the seed circle sampled as a 16-gon.</param>
/// <param name="AnchorX">The normalized centre the <paramref name="ShapePoints"/> are relative to — the
/// SEED centre, i.e. the hold's existing <see cref="Hold.X"/>, so the points can be stored without moving
/// the hold.</param>
/// <param name="AnchorY">The normalized centre Y the shape points are relative to.</param>
/// <param name="ShapePoints">The same polygon in the <see cref="Hold.ShapePoints"/> convention (see
/// <see cref="Blocwerk.Core.Detection.Outlines.HoldOutlineGeometry"/>), ready to assign to a hold whose centre is
/// (<paramref name="AnchorX"/>, <paramref name="AnchorY"/>). Null for a circle fallback, meaning "keep
/// the plain circle".</param>
/// <param name="AreaPx">The outlined area in pixels of the full image.</param>
/// <param name="Bounds">The polygon's axis-aligned bounding box, normalized per axis.</param>
/// <param name="Confidence">0..1 — how much the outline can be trusted. Circle fallbacks stay ≤ 0.2.</param>
/// <param name="Method">How the outline was obtained.</param>
/// <param name="Fingerprint">The appearance descriptor, measured on the outlined pixels (on the seed disc
/// for a circle fallback). Always measured on the FILLED outline (holes included) — see
/// <paramref name="ShapeHoles"/>.</param>
/// <param name="ShapeHoles">Significant interior holes of the outline (a donut's through-hole, a deep pocket),
/// each ring in the same convention and relative to the same anchor as <paramref name="ShapePoints"/>. Null for
/// a solid hold and for a circle fallback. <paramref name="AreaPx"/> already excludes them; the
/// <paramref name="Fingerprint"/> deliberately does NOT, so a hold fingerprints the same whether or not its hole
/// was detected on a given photo (a pocket's floor can read as hold on one photo and as shadow on the next).</param>
public sealed record HoldOutlineResult(
    IReadOnlyList<NormalizedPoint> Polygon,
    double AnchorX,
    double AnchorY,
    List<ShapePoint>? ShapePoints,
    double AreaPx,
    HoldOutlineBounds Bounds,
    double Confidence,
    HoldOutlineMethod Method,
    HoldFingerprint Fingerprint,
    List<List<ShapePoint>>? ShapeHoles = null);
