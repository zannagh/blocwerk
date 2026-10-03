using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>What the clean-up did to one hold.</summary>
public enum HoldShapeChangeKind
{
    /// <summary>Same outline, smoothed (spikes, incuts, jagged edge).</summary>
    Smoothed = 0,

    /// <summary>Clipped or shrunk toward the centre to clear its neighbours.</summary>
    Clipped = 1,

    /// <summary>Back to the plain circle (jagged beyond repair, or no room for the outline).</summary>
    Circle = 2,

    /// <summary>Plain circle at a reduced radius (the largest that clears its neighbours).</summary>
    ShrunkCircle = 3,

    /// <summary>A hold that could not be cleared of an overlap; it only appears when its outline had to be dropped.</summary>
    Unresolved = 4,
}

/// <summary>One hold's new geometry. Null <see cref="Shape"/> means the plain circle.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Shape">The new outline offsets, or null for the circle.</param>
/// <param name="Radius">The new radius.</param>
/// <param name="Kind">What happened.</param>
public sealed record HoldShapeChange(Guid HoldId, List<ShapePoint>? Shape, double Radius, HoldShapeChangeKind Kind);

/// <summary>A panel's planned clean-up.</summary>
/// <param name="Changes">The holds whose shape or radius change.</param>
/// <param name="Unresolved">Holds that still overlap something after the clean-up (changed or not).</param>
/// <param name="LockedOverlaps">Overlapping pairs of holds the clean-up may not touch (manual / hand-drawn).</param>
public sealed record HoldShapePlan(
    List<HoldShapeChange> Changes, List<Guid> Unresolved, List<(Guid A, Guid B)> LockedOverlaps);

/// <summary>
/// Entity-level glue of <see cref="HoldShapeSmoother"/> and <see cref="HoldShapeOverlapResolver"/>: decides
/// which holds may be touched (auto-detected, non-manual holds - traced outlines AND plain circles; hand-drawn
/// and manually placed holds never) and turns the decisions into changes. Pure: reads holds, writes nothing.
/// </summary>
public static class HoldShapeCleanup
{
    private const int MaxPasses = 6;

    /// <summary>True for an automatically traced outline: auto-detected, not virtual, not hand-edited, with a polygon.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether the clean-up may change its outline.</returns>
    public static bool IsCleanable(Hold hold) => IsResolvable(hold) && hold.ShapePoints is { Count: >= 3 };

    /// <summary>True for an automatic hold the clean-up may change at all: an auto outline or an auto plain circle.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether it is resolvable.</returns>
    public static bool IsResolvable(Hold hold) =>
        !hold.IsVirtual && hold.IsAutoDetected && hold.OutlineSource != HoldOutlineSource.Manual;

    /// <summary>Plans the clean-up of one panel's stored shapes.</summary>
    /// <param name="panelHolds">Every live hold on the panel photo.</param>
    /// <param name="aspect">Photo width / height (1 when unknown).</param>
    /// <returns>The holds whose shape or radius would change, in ascending id order.</returns>
    public static List<HoldShapeChange> Plan(IReadOnlyList<Hold> panelHolds, double aspect = 1) =>
        PlanDetailed(panelHolds, aspect).Changes;

    /// <summary>Plans the clean-up of one panel and also reports what could not be resolved.</summary>
    /// <param name="panelHolds">Every live hold on the panel photo.</param>
    /// <param name="aspect">Photo width / height (1 when unknown).</param>
    /// <returns>The plan.</returns>
    public static HoldShapePlan PlanDetailed(IReadOnlyList<Hold> panelHolds, double aspect = 1)
    {
        ArgumentNullException.ThrowIfNull(panelHolds);
        var resolvable = panelHolds.Where(IsResolvable).ToDictionary(h => h.Id);
        var smoothed = resolvable.Values.ToDictionary(h => h.Id, h => h.ShapePoints is { Count: >= 3 } shape ? HoldShapeSmoother.Smooth(shape, aspect) : null);
        var current = panelHolds.Select(h => resolvable.ContainsKey(h.Id)
            ? new HoldShapeInput(h.Id, h.X, h.Y, h.Radius, smoothed[h.Id], Locked: false)
            : Locked(h)).ToList();

        // Run to a fixpoint: every pass only shrinks things, so it converges, and a re-run on the result changes nothing.
        List<HoldShapeResolution> last = [];
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            last = HoldShapeOverlapResolver.Resolve(current, allowRadiusShrink: true, aspect);
            var results = last.ToDictionary(r => r.Id);
            var changed = current.Any(i => results.TryGetValue(i.Id, out var r) && Differs(i.Shape, i.Radius, r));
            current = current.Select(i => results.TryGetValue(i.Id, out var r) ? i with { Shape = r.Shape, Radius = r.Radius } : i).ToList();
            if (!changed)
            {
                break;
            }
        }

        var changes = new List<HoldShapeChange>();
        foreach (var r in last)
        {
            var hold = resolvable[r.Id];
            if (Differs(hold.ShapePoints, hold.Radius, r))
            {
                changes.Add(new HoldShapeChange(r.Id, r.Shape?.ToList(), r.Radius, KindOf(hold, r, smoothed[r.Id])));
            }
        }

        var unresolved = last.Where(r => r.Fit == HoldShapeFit.Unresolved).Select(r => r.Id).ToList();
        var locked = panelHolds.Where(h => !resolvable.ContainsKey(h.Id) && !h.IsVirtual).ToList();
        return new HoldShapePlan(changes, unresolved, HoldShapeLockedOverlaps.Find(locked));
    }

    /// <summary>
    /// Resolves overlaps for freshly traced outlines of one panel against every hold on it (every hold that has an
    /// outline result is adjustable, every other hold is a locked obstacle). A contour is clipped, else a circle; a
    /// circle shrinks (when allowed) to the largest radius that clears.
    /// </summary>
    /// <param name="panelHolds">Every hold on the panel photo.</param>
    /// <param name="outlines">The new outlines by hold.</param>
    /// <param name="allowRadiusShrink">False when radii must not change.</param>
    /// <param name="aspect">Photo width / height.</param>
    /// <returns>The adjusted outline and radius per hold in <paramref name="outlines"/>.</returns>
    public static Dictionary<Hold, (HoldOutlineResult Outline, double Radius)> ResolveOutlines(
        IReadOnlyList<Hold> panelHolds,
        IReadOnlyDictionary<Hold, HoldOutlineResult> outlines,
        bool allowRadiusShrink,
        double aspect = 1)
    {
        ArgumentNullException.ThrowIfNull(panelHolds);
        ArgumentNullException.ThrowIfNull(outlines);
        var inputs = panelHolds
            .Select(h => outlines.TryGetValue(h, out var o)
                ? new HoldShapeInput(h.Id, h.X, h.Y, h.Radius, IsContour(o) ? o.ShapePoints : null, Locked: false)
                : Locked(h))
            .ToList();
        var byId = outlines.Keys.ToDictionary(h => h.Id);
        var resolved = new Dictionary<Hold, (HoldOutlineResult, double)>();
        foreach (var r in HoldShapeOverlapResolver.Resolve(inputs, allowRadiusShrink, aspect))
        {
            var hold = byId[r.Id];
            var outline = outlines[hold];
            resolved[hold] = (Adjusted(outline, r), r.Radius);
        }

        return resolved;
    }

    private static HoldOutlineResult Adjusted(HoldOutlineResult outline, HoldShapeResolution r)
    {
        if (!IsContour(outline))
        {
            return outline;
        }

        if (r.Shape is null)
        {
            return outline with
            {
                Method = HoldOutlineMethod.CircleFallback,
                ShapePoints = null,
                ShapeHoles = null,
                Confidence = Math.Min(outline.Confidence, HoldOutlineRefiner.CircleConfidenceCeiling),
            };
        }

        return r.Fit == HoldShapeFit.Unchanged
            ? outline
            : outline with { ShapePoints = r.Shape.ToList(), ShapeHoles = HoldShapeHoles.Inside(outline.ShapeHoles, r.Shape) };
    }

    private static HoldShapeInput Locked(Hold h) => new(h.Id, h.X, h.Y, h.Radius, h.ShapePoints, Locked: true);

    private static bool IsContour(HoldOutlineResult o) =>
        o.Method != HoldOutlineMethod.CircleFallback && o.ShapePoints is { Count: >= 3 };

    private static bool Differs(IReadOnlyList<ShapePoint>? shape, double radius, HoldShapeResolution r)
    {
        if (Math.Abs(radius - r.Radius) > 1e-9)
        {
            return true;
        }

        if (shape is null || r.Shape is null)
        {
            return !(shape is null && r.Shape is null);
        }

        return shape.Count != r.Shape.Count
               || shape.Zip(r.Shape).Any(p => Math.Abs(p.First.Dx - p.Second.Dx) > 1e-9 || Math.Abs(p.First.Dy - p.Second.Dy) > 1e-9);
    }

    /// <summary>Names the net effect: outline kept (smoothed or trimmed), back to a circle, or a smaller circle.</summary>
    private static HoldShapeChangeKind KindOf(Hold before, HoldShapeResolution r, List<ShapePoint>? smoothedBefore)
    {
        if (r.Shape is not null)
        {
            var onlySmoothed = smoothedBefore is not null && !Differs(smoothedBefore, r.Radius, r);
            return onlySmoothed ? HoldShapeChangeKind.Smoothed : HoldShapeChangeKind.Clipped;
        }

        return r.Radius < before.Radius - 1e-9 ? HoldShapeChangeKind.ShrunkCircle : HoldShapeChangeKind.Circle;
    }
}
