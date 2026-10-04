using System.Globalization;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Finds holds on one photo that are probably the same physical hold recorded twice. Pure: reads holds, writes nothing.
/// Three rules (a virtual hold counts as hand-made; against an automatic hold it also matches when its own centre lies inside the detection): an automatic hold whose CENTRE lies inside a hand-made hold; two automatic
/// holds with IoU >= <see cref="AutoIou"/> or one mostly inside the other (>= <see cref="AutoContainment"/>) and a similar
/// colour; two hand-made holds with IoU >= <see cref="HandIou"/> or containment >= <see cref="HandContainment"/>.
/// </summary>
public static class HoldDuplicateFinder
{
    /// <summary>Automatic pair: shared area over combined area at least this.</summary>
    public const double AutoIou = 0.5;

    /// <summary>Automatic pair: shared area over the smaller hold at least this.</summary>
    public const double AutoContainment = 0.8;

    /// <summary>Hand-made pair: shared area over combined area at least this (stricter: people place them on purpose).</summary>
    public const double HandIou = 0.6;

    /// <summary>Hand-made pair: shared area over the smaller hold at least this.</summary>
    public const double HandContainment = 0.85;

    private const double MaxRgbDistance = 60;

    /// <summary>The candidates of one photo, most confident first, each with the hold to keep as <c>HoldA</c>.</summary>
    /// <param name="panelHolds">Every live hold on the photo.</param>
    /// <param name="boulderCounts">How many boulders use each hold (a missing hold counts 0); decides which one is kept.</param>
    /// <returns>The candidates.</returns>
    public static List<HoldDuplicateCandidate> Find(IReadOnlyList<Hold> panelHolds, IReadOnlyDictionary<Guid, int>? boulderCounts = null)
    {
        ArgumentNullException.ThrowIfNull(panelHolds);
        var items = panelHolds.OrderBy(h => h.Id).Select(h => (Hold: h, Poly: HoldShapePolygon.Of(h))).ToList();
        var boxes = items.Select(i => (MinX: i.Poly.Min(p => p.X), MaxX: i.Poly.Max(p => p.X), MinY: i.Poly.Min(p => p.Y), MaxY: i.Poly.Max(p => p.Y))).ToList();
        var found = new List<HoldDuplicateCandidate>();
        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                if (boxes[i].MaxX < boxes[j].MinX || boxes[j].MaxX < boxes[i].MinX || boxes[i].MaxY < boxes[j].MinY || boxes[j].MaxY < boxes[i].MinY)
                {
                    continue;
                }

                if (Judge(items[i].Hold, items[i].Poly, items[j].Hold, items[j].Poly) is not var (kind, confidence, iou, containment))
                {
                    continue;
                }

                var kept = HoldMergeRules.PickKeeper(items[i].Hold, Count(boulderCounts, items[i].Hold), items[j].Hold, Count(boulderCounts, items[j].Hold));
                var other = kept == items[i].Hold ? items[j].Hold : items[i].Hold;
                found.Add(new HoldDuplicateCandidate(kept.Id, other.Id, kind, confidence, iou, containment));
            }
        }

        return [.. found.OrderByDescending(c => c.Confidence).ThenBy(c => c.HoldA).ThenBy(c => c.HoldB)];
    }

    /// <summary>True when two colours may be the same hold's: either unknown, equal by name, or close as #rrggbb.</summary>
    /// <param name="a">One colour.</param>
    /// <param name="b">The other.</param>
    /// <returns>Whether they are similar.</returns>
    public static bool SimilarColor(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return true;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryRgb(a, out var x) && TryRgb(b, out var y)
            && Math.Sqrt(((x.R - y.R) * (x.R - y.R)) + ((x.G - y.G) * (x.G - y.G)) + ((x.B - y.B) * (x.B - y.B))) <= MaxRgbDistance;
    }

    private static int Count(IReadOnlyDictionary<Guid, int>? counts, Hold hold) =>
        counts is not null && counts.TryGetValue(hold.Id, out var n) ? n : 0;

    private static (HoldDuplicateKind Kind, double Confidence, double Iou, double Containment)? Judge(Hold a, P2[] pa, Hold b, P2[] pb)
    {
        bool handA = HoldMergeRules.IsHandMade(a);
        bool handB = HoldMergeRules.IsHandMade(b);
        var (areaA, areaB, shared) = ShapeOverlapArea.Measure(pa, pb);
        if (shared <= 0 || areaA <= 0 || areaB <= 0)
        {
            return null;
        }

        double iou = shared / (areaA + areaB - shared);
        double containment = shared / Math.Min(areaA, areaB);
        if (handA != handB)
        {
            var auto = handA ? b : a;
            var autoArea = handA ? areaB : areaA;
            var hand = handA ? a : b;
            var handPoly = handA ? pa : pb;
            var autoPoly = handA ? pb : pa;
            bool inside = ShapeGeometry.Contains(handPoly, new P2(auto.X, auto.Y))
                || (hand.IsVirtual && ShapeGeometry.Contains(autoPoly, new P2(hand.X, hand.Y)));
            return inside
                ? (HoldDuplicateKind.InsideHandPlaced, 0.6 + (0.4 * Math.Min(1, shared / autoArea)), iou, containment)
                : null;
        }

        if (handA)
        {
            return iou >= HandIou || containment >= HandContainment
                ? (HoldDuplicateKind.HandPlacedPair, 0.9 * Math.Min(1, Math.Max(iou, 0.9 * containment)), iou, containment)
                : null;
        }

        return (iou >= AutoIou || containment >= AutoContainment) && SimilarColor(a.Color, b.Color)
            ? (HoldDuplicateKind.NearDuplicateAutomatic, Math.Min(1, Math.Max(iou, 0.9 * containment)), iou, containment)
            : null;
    }

    private static bool TryRgb(string text, out (int R, int G, int B) rgb)
    {
        rgb = default;
        var s = text.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        rgb = ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        return true;
    }
}
