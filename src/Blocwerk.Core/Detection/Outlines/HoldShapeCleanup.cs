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

    /// <summary>Plain circle at a reduced radius.</summary>
    ShrunkCircle = 3,

    /// <summary>Minimum circle that still overlaps a locked hold.</summary>
    Unresolved = 4,
}

/// <summary>One hold's new geometry. Null <see cref="Shape"/> means the plain circle.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Shape">The new outline offsets, or null for the circle.</param>
/// <param name="Radius">The new radius.</param>
/// <param name="Kind">What happened.</param>
public sealed record HoldShapeChange(Guid HoldId, List<ShapePoint>? Shape, double Radius, HoldShapeChangeKind Kind);

/// <summary>
/// Entity-level glue of <see cref="HoldShapeSmoother"/> and <see cref="HoldShapeOverlapResolver"/>: decides
/// which holds may be touched (auto-traced outlines only - hand-drawn and manually placed holds never) and
/// turns the decisions into changes. Pure: reads holds, writes nothing.
/// </summary>
public static class HoldShapeCleanup
{
    /// <summary>True for an automatically traced outline: auto-detected, not virtual, not hand-edited, with a polygon.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Whether the clean-up may change it.</returns>
    public static bool IsCleanable(Hold hold) =>
        !hold.IsVirtual
        && hold.IsAutoDetected
        && hold.OutlineSource != HoldOutlineSource.Manual
        && hold.ShapePoints is { Count: >= 3 };

    /// <summary>Plans the clean-up of one panel's stored shapes.</summary>
    /// <param name="panelHolds">Every live hold on the panel photo.</param>
    /// <param name="aspect">Photo width / height (1 when unknown).</param>
    /// <returns>The holds whose shape or radius would change, in ascending id order.</returns>
    public static List<HoldShapeChange> Plan(IReadOnlyList<Hold> panelHolds, double aspect = 1)
    {
        ArgumentNullException.ThrowIfNull(panelHolds);
        var smoothed = panelHolds.Where(IsCleanable).ToDictionary(h => h.Id, h => HoldShapeSmoother.Smooth(h.ShapePoints!, aspect));
        var inputs = panelHolds
            .Select(h => new HoldShapeInput(
                h.Id, h.X, h.Y, h.Radius, smoothed.TryGetValue(h.Id, out var s) ? s : h.ShapePoints, Locked: !smoothed.ContainsKey(h.Id)))
            .ToList();
        var byId = panelHolds.ToDictionary(h => h.Id);
        var changes = new List<HoldShapeChange>();
        foreach (var r in HoldShapeOverlapResolver.Resolve(inputs, allowRadiusShrink: true, aspect))
        {
            var hold = byId[r.Id];
            if (!Differs(hold, r))
            {
                continue;
            }

            changes.Add(new HoldShapeChange(r.Id, r.Shape?.ToList(), r.Radius, KindOf(r)));
        }

        return changes;
    }

    /// <summary>
    /// Resolves overlaps for freshly traced outlines of one panel against every hold on it (stored holds are
    /// locked obstacles). Radii are only ever reduced for the holds that were just traced.
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
            .Select(h => outlines.TryGetValue(h, out var o) && IsContour(o)
                ? new HoldShapeInput(h.Id, h.X, h.Y, h.Radius, o.ShapePoints, Locked: false)
                : new HoldShapeInput(h.Id, h.X, h.Y, h.Radius, h.ShapePoints, Locked: true))
            .ToList();
        var byId = outlines.Keys.ToDictionary(h => h.Id);
        var resolved = new Dictionary<Hold, (HoldOutlineResult, double)>();
        foreach (var r in HoldShapeOverlapResolver.Resolve(inputs, allowRadiusShrink, aspect))
        {
            var hold = byId[r.Id];
            var outline = outlines[hold];
            resolved[hold] = r.Shape is null
                ? (outline with { Method = HoldOutlineMethod.CircleFallback, ShapePoints = null, ShapeHoles = null, Confidence = Math.Min(outline.Confidence, 0.2) }, r.Radius)
                : (r.Fit == HoldShapeFit.Unchanged ? outline : outline with { ShapePoints = r.Shape.ToList(), ShapeHoles = null }, r.Radius);
        }

        return resolved;
    }

    private static bool IsContour(HoldOutlineResult o) =>
        o.Method != HoldOutlineMethod.CircleFallback && o.ShapePoints is { Count: >= 3 };

    private static bool Differs(Hold hold, HoldShapeResolution r)
    {
        if (Math.Abs(hold.Radius - r.Radius) > 1e-9)
        {
            return true;
        }

        var before = hold.ShapePoints;
        if (before is null || r.Shape is null)
        {
            return !(before is null && r.Shape is null);
        }

        return before.Count != r.Shape.Count
               || before.Zip(r.Shape).Any(p => Math.Abs(p.First.Dx - p.Second.Dx) > 1e-9 || Math.Abs(p.First.Dy - p.Second.Dy) > 1e-9);
    }

    private static HoldShapeChangeKind KindOf(HoldShapeResolution r) => r.Fit switch
    {
        HoldShapeFit.Shrunk => HoldShapeChangeKind.Clipped,
        HoldShapeFit.ShrunkCircle => HoldShapeChangeKind.ShrunkCircle,
        HoldShapeFit.Unresolved => HoldShapeChangeKind.Unresolved,
        _ => r.Shape is null ? HoldShapeChangeKind.Circle : HoldShapeChangeKind.Smoothed,
    };
}
