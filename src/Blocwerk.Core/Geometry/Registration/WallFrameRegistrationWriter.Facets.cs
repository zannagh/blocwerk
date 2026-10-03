// <copyright file="WallFrameRegistrationWriter.Facets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>Facet identity: which new facet is which reference facet, and what is carried over.</summary>
public static partial class WallFrameRegistrationWriter
{
    /// <summary>
    /// New facet id → the reference facet it continues: the one most of its registration markers sat on,
    /// facing the same way. Facets with the most such markers claim first; a reference facet is claimed once.
    /// </summary>
    private static Dictionary<string, string> MatchFacets(
        WallGeometryDocument solved, WallGeometryDocument reference, IReadOnlyList<int> usedIds, RigidTransform3D t)
    {
        var used = usedIds.ToHashSet();
        var candidates = solved.Segments.SelectMany(s => s.Facets)
            .Select(f => (Facet: f, Votes: solved.Markers
                .Where(m => m.Facet == f.Id && used.Contains(m.Id))
                .Select(m => reference.FindMarker(m.Id)?.Facet)
                .OfType<string>()
                .GroupBy(id => id)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .FirstOrDefault()))
            .Where(c => c.Votes is not null)
            .OrderByDescending(c => c.Votes!.Count())
            .ToList();

        var claims = new Dictionary<string, string>();
        foreach (var (facet, votes) in candidates)
        {
            var target = reference.FindFacet(votes!.Key)?.Facet;
            if (target is null || !MarkerWorldCorners.HasFrame(target) || claims.ContainsValue(target.Id) || !SameFacing(facet, target, t))
            {
                continue;
            }

            claims[facet.Id] = target.Id;
        }

        ClaimBySegment(solved, reference, claims, t);
        return claims;
    }

    /// <summary>
    /// A new facet none of whose markers could tie it (all of them changed in the plan: moved, mirrored) continues its
    /// segment's reference facet when that is the segment's only unclaimed one facing the same way: a segment is one
    /// surface, so a re-solve never leaves it as two facets (the old one carried over beside the new one).
    /// </summary>
    private static void ClaimBySegment(
        WallGeometryDocument solved, WallGeometryDocument reference, Dictionary<string, string> claims, RigidTransform3D t)
    {
        foreach (var segment in solved.Segments)
        {
            var open = segment.Facets.Where(f => !claims.ContainsKey(f.Id)).ToList();
            var free = reference.Segments.Where(s => s.Index == segment.Index).SelectMany(s => s.Facets)
                .Where(f => MarkerWorldCorners.HasFrame(f) && !claims.ContainsValue(f.Id))
                .ToList();
            if (open.Count == 1 && free.Count == 1 && SameFacing(open[0], free[0], t))
            {
                claims[open[0].Id] = free[0].Id;
            }
        }
    }

    private static bool SameFacing(WallGeometryFacet facet, WallGeometryFacet target, RigidTransform3D t) =>
        facet.Normal is not { Length: 3 } || target.Normal is not { Length: 3 }
        || Vec3.AngleDeg(t.Rotate(facet.Normal), target.Normal) <= MatchNormalToleranceDeg;

    /// <summary>Every new facet's final id: the claimed reference id, else its own unless that is taken.</summary>
    private static Dictionary<string, string> FinalIds(JsonObject root, Dictionary<string, string> claims, List<string> carried)
    {
        var taken = claims.Values.Concat(carried).ToHashSet(StringComparer.Ordinal);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (segment, facet) in SegmentFacets(root))
        {
            var id = facet["id"]?.GetValue<string>() ?? string.Empty;
            if (claims.TryGetValue(id, out var claimed))
            {
                ids[id] = claimed;
                continue;
            }

            var final = taken.Contains(id) ? FreshId(segment, taken) : id;
            taken.Add(final);
            ids[id] = final;
        }

        return ids;
    }

    private static string FreshId(int segment, HashSet<string> taken)
    {
        for (var c = 'a'; c <= 'z'; c++)
        {
            if (!taken.Contains($"{segment}{c}"))
            {
                return $"{segment}{c}";
            }
        }

        return $"f{Guid.NewGuid():N}"[..16];
    }

    /// <summary>Markers move to their facet's final id; on a claimed facet their plane corners are re-projected into its rebased frame.</summary>
    private static void RewriteMarkers(
        JsonObject root, Dictionary<string, WallGeometryFacet> rebased, Dictionary<string, string> renamed, Dictionary<int, double[][]> world)
    {
        foreach (var marker in (root["markers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = marker["id"]!.GetValue<int>();
            var facet = marker["facet"]?.GetValue<string>() ?? string.Empty;
            if (!renamed.TryGetValue(facet, out var final))
            {
                continue;
            }

            marker["facet"] = final;
            if (!world.TryGetValue(id, out var corners))
            {
                continue;
            }

            marker["cornersWorldMm"] = new JsonArray(corners.Select(c => (JsonNode?)Array(c, 2)).ToArray());
            if (rebased.TryGetValue(facet, out var frame))
            {
                marker["cornersPlaneMm"] = new JsonArray(corners.Select(c => (JsonNode?)Array(ToPlane(frame, c), 2)).ToArray());
            }
        }
    }

    /// <summary>Carries unphotographed reference facets and unchanged reference markers over. Returns the carried marker ids.</summary>
    private static List<int> Carry(
        JsonObject root, JsonObject referenceRoot, WallGeometryDocument reference, List<string> carried, IReadOnlySet<int>? carryIds)
    {
        var segments = ChildArray(root, "segments");
        foreach (var id in carried)
        {
            var (segment, _) = reference.FindFacet(id)!.Value;
            var target = segments.OfType<JsonObject>().FirstOrDefault(s => s["index"]?.GetValue<int>() == segment.Index);
            if (target is null)
            {
                target = referenceRoot["segments"]!.AsArray().OfType<JsonObject>().First(s => s["index"]?.GetValue<int>() == segment.Index).DeepClone().AsObject();
                target["facets"] = new JsonArray();
                segments.Add(target);
            }

            var node = Facets(referenceRoot).First(f => f["id"]!.GetValue<string>() == id).DeepClone();
            (target["facets"] as JsonArray)!.Add(node);
        }

        var markers = ChildArray(root, "markers");
        var present = markers.OfType<JsonObject>().Select(m => m["id"]!.GetValue<int>()).ToHashSet();
        var facets = Facets(root).Select(f => f["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var carriedMarkers = new List<int>();
        foreach (var marker in (referenceRoot["markers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = marker["id"]!.GetValue<int>();
            if (present.Contains(id) || (carryIds is not null && !carryIds.Contains(id)) || !facets.Contains(marker["facet"]?.GetValue<string>() ?? string.Empty))
            {
                continue;
            }

            var copy = marker.DeepClone().AsObject();
            copy["carried"] = true;
            markers.Add(copy);
            carriedMarkers.Add(id);
        }

        return carriedMarkers;
    }

    /// <summary>A claimed facet's (solved) extent grows over the reference markers carried onto it (not re-photographed, still there).</summary>
    private static void CoverCarried(JsonObject root, HashSet<string> claimed, List<int> carriedMarkers)
    {
        var carried = carriedMarkers.ToHashSet();
        var markers = (root["markers"] as JsonArray ?? []).OfType<JsonObject>().Where(m => carried.Contains(m["id"]!.GetValue<int>())).ToList();
        foreach (var facet in Facets(root).Where(f => claimed.Contains(f["id"]!.GetValue<string>())))
        {
            var id = facet["id"]!.GetValue<string>();
            var corners = markers.Where(m => m["facet"]?.GetValue<string>() == id)
                .SelectMany(m => (m["cornersPlaneMm"] as JsonArray ?? []).Select(Numbers).OfType<double[]>().Where(c => c.Length == 2))
                .ToList();
            if (corners.Count == 0 || facet["extentMm"] is not JsonObject e)
            {
                continue;
            }

            // Same frame, a bigger extent: an outline's fold clips stay, its other sides follow the extent.
            FacetPlaneGeometry.Rewrite(facet, p => p, ExtentRect(Rect(e), corners, ExtentMarginMm));
        }
    }

    private static void RefreshMarkerIds(JsonObject root)
    {
        var markers = (root["markers"] as JsonArray ?? []).OfType<JsonObject>()
            .OrderBy(m => m["id"]!.GetValue<int>())
            .ToList();
        root["markers"] = new JsonArray(markers.Select(m => (JsonNode?)m.DeepClone()).ToArray());
        foreach (var facet in Facets(root))
        {
            var id = facet["id"]!.GetValue<string>();
            facet["markerIds"] = Ints(markers.Where(m => m["facet"]?.GetValue<string>() == id).Select(m => m["id"]!.GetValue<int>()).ToList());
        }
    }

    private static double[] ToPlane(WallGeometryFacet frame, double[] x)
    {
        var d = Vec3.Sub(x, frame.Origin!);
        return [Vec3.Dot(d, frame.U!), Vec3.Dot(d, frame.V!)];
    }

    private static PlaneRectMm Rect(JsonObject e) => new(Num(e, "aMin"), Num(e, "aMax"), Num(e, "bMin"), Num(e, "bMax"));

    private static JsonObject Json(PlaneRectMm r) => new()
    {
        ["aMin"] = Math.Round(r.AMin, 1),
        ["aMax"] = Math.Round(r.AMax, 1),
        ["bMin"] = Math.Round(r.BMin, 1),
        ["bMax"] = Math.Round(r.BMax, 1),
    };

    private static PlaneRectMm ExtentRect(PlaneRectMm? old, IEnumerable<double[]> plane, double margin)
    {
        var points = plane.Select(p => (p[0], p[1])).ToList();
        var bounds = PlaneRectMm.Bounds(points);
        var grown = bounds is { } b
            ? new PlaneRectMm(b.AMin - margin, b.AMax + margin, b.BMin - margin, b.BMax + margin)
            : (PlaneRectMm?)null;
        var union = (old, grown) switch
        {
            ({ } o, { } g) => new PlaneRectMm(Math.Min(o.AMin, g.AMin), Math.Max(o.AMax, g.AMax), Math.Min(o.BMin, g.BMin), Math.Max(o.BMax, g.BMax)),
            ({ } o, null) => o,
            (null, { } g) => g,
            _ => new PlaneRectMm(-ExtentMarginMm, ExtentMarginMm, -ExtentMarginMm, ExtentMarginMm),
        };
        return new PlaneRectMm(Math.Round(union.AMin, 1), Math.Round(union.AMax, 1), Math.Round(union.BMin, 1), Math.Round(union.BMax, 1));
    }
}
