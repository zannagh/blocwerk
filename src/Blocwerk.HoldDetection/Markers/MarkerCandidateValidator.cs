using Blocwerk.Core.Abstractions;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>A decoded but not yet validated marker: id + pixel corners (TL, TR, BR, BL).</summary>
internal readonly record struct MarkerCandidate(int Id, IReadOnlyList<MarkerPoint> CornersPx);

/// <summary>Accepted and rejected candidates of one image.</summary>
internal sealed record MarkerValidationOutcome(
    IReadOnlyList<DetectedMarker> Accepted,
    IReadOnlyList<RejectedMarkerCandidate> Rejected,
    bool Suspicious,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Pure validation of decoded candidates, applied in this order: nested same-id copies of one marker
/// collapsed (<see cref="NestedMarkerCollapser"/>) → id allow-list → min side →
/// max edge ratio → one candidate per id (the rest are rejected and the image is flagged
/// suspicious). DICT_4X4_50 has a low Hamming distance and produced real false positives on the
/// wall photos (ids 37/38, one id four times in a frame, a 12 px id 17), so none of this is optional.
/// </summary>
internal static class MarkerCandidateValidator
{
    public static MarkerValidationOutcome Validate(
        IReadOnlyList<MarkerCandidate> candidates,
        MarkerDetectionOptions options,
        int imageWidth,
        int imageHeight)
    {
        var rejected = new List<RejectedMarkerCandidate>();
        var survivors = new List<DetectedMarker>();
        foreach (var candidate in NestedMarkerCollapser.Collapse(candidates))
        {
            var marker = ToMarker(candidate, imageWidth, imageHeight);
            var reject = CheckSingle(marker, options);
            if (reject is not null)
            {
                rejected.Add(reject);
            }
            else
            {
                survivors.Add(marker);
            }
        }

        var warnings = new List<string>();
        var accepted = new List<DetectedMarker>();
        foreach (var group in survivors.GroupBy(m => m.Id).OrderBy(g => g.Key))
        {
            var ranked = group.OrderByDescending(Quality).ToList();
            accepted.Add(ranked[0]);
            if (ranked.Count == 1)
            {
                continue;
            }

            warnings.Add($"id {group.Key} decoded {ranked.Count}x in one image; kept the best, likely false positives.");
            foreach (var loser in ranked.Skip(1))
            {
                rejected.Add(Reject(
                    loser,
                    MarkerRejectionReason.DuplicateId,
                    $"id {loser.Id} also at ({ranked[0].CenterPx.X:F0},{ranked[0].CenterPx.Y:F0}) with a better score"));
            }
        }

        return new MarkerValidationOutcome(accepted, rejected, warnings.Count > 0, warnings);
    }

    /// <summary>Mean edge length and longest/shortest edge ratio of a quad.</summary>
    public static (double SidePx, double EdgeRatio) Measure(IReadOnlyList<MarkerPoint> corners)
    {
        var min = double.MaxValue;
        var max = 0.0;
        var sum = 0.0;
        for (var i = 0; i < 4; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % 4];
            var len = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
            sum += len;
            min = Math.Min(min, len);
            max = Math.Max(max, len);
        }

        var ratio = min > 1e-9 ? max / min : double.PositiveInfinity;
        return (sum / 4.0, ratio);
    }

    /// <summary>Bigger and squarer wins: a duplicate is almost always a small oblique misdecode.</summary>
    private static double Quality(DetectedMarker m) => m.SidePx / m.EdgeRatio;

    private static RejectedMarkerCandidate? CheckSingle(DetectedMarker marker, MarkerDetectionOptions options)
    {
        if (!options.AllowedIds.Contains(marker.Id))
        {
            return Reject(marker, MarkerRejectionReason.IdNotAllowed, $"id {marker.Id} is not in the allowed set");
        }

        if (marker.SidePx < options.MinSidePx)
        {
            return Reject(marker, MarkerRejectionReason.TooSmall, $"side {marker.SidePx:F1}px < {options.MinSidePx:F1}px");
        }

        if (marker.EdgeRatio > options.MaxEdgeRatio)
        {
            return Reject(marker, MarkerRejectionReason.TooOblique, $"edge ratio {marker.EdgeRatio:F2} > {options.MaxEdgeRatio:F2}");
        }

        return null;
    }

    private static RejectedMarkerCandidate Reject(DetectedMarker m, MarkerRejectionReason reason, string detail) =>
        new(m.Id, m.CornersPx, m.SidePx, m.EdgeRatio, reason, detail);

    private static DetectedMarker ToMarker(MarkerCandidate candidate, int imageWidth, int imageHeight)
    {
        var (side, ratio) = Measure(candidate.CornersPx);
        return new DetectedMarker
        {
            Id = candidate.Id,
            CornersPx = candidate.CornersPx,
            CornersNormalized = candidate.CornersPx
                .Select(c => new MarkerPoint(c.X / imageWidth, c.Y / imageHeight))
                .ToList(),
            SidePx = side,
            EdgeRatio = ratio,
        };
    }
}
