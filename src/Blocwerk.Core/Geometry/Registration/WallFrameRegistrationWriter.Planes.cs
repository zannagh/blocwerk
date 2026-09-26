// <copyright file="WallFrameRegistrationWriter.Planes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>What the plane-based rewrite of a feature model did.</summary>
/// <param name="Json">The document to import (reference facet ids, rebased frames, unphotographed facets carried).</param>
/// <param name="Claims">New facet id → the reference facet it continues.</param>
public sealed record PlaneRegistrationResult(string Json, IReadOnlyDictionary<string, string> Claims);

/// <summary>
/// Facet identity for a model solved WITHOUT markers (<c>solve-sfm</c>, anchored): it already lies in the reference
/// model's world (the solver fitted it on anchor photos), so nothing moves; a new facet claims the reference facet whose
/// plane it continues — same facing, near the same plane, overlapping outlines — and from there the marker path's rules
/// apply unchanged: the reference id, the new plane in a rebased frame (<see cref="Rebase"/>), unphotographed reference
/// facets carried over, and the segments regrouped under the reference's so the bound wall segments stay bound.
/// </summary>
public static partial class WallFrameRegistrationWriter
{
    /// <summary>A new plane further than this from a reference facet's plane (at its centre) is another surface, mm.</summary>
    private const double PlaneMatchMm = 150;

    /// <summary>Rewrites an anchored feature model onto the reference's facet ids. No markers are carried.</summary>
    /// <param name="solvedJson">The solve-sfm document (already in the reference world).</param>
    /// <param name="referenceJson">The active model.</param>
    /// <param name="referenceModelId">Its id, for the stamp.</param>
    /// <returns>The rewritten document and the claims.</returns>
    public static PlaneRegistrationResult RewriteFeatures(string solvedJson, string referenceJson, Guid referenceModelId)
    {
        var solved = WallGeometryDocument.Parse(solvedJson);
        var reference = WallGeometryDocument.Parse(referenceJson);
        var root = JsonNode.Parse(solvedJson)!.AsObject();
        var referenceRoot = JsonNode.Parse(referenceJson)!.AsObject();

        var claims = MatchFacetsByPlane(solved, reference);
        var carried = reference.Segments.SelectMany(s => s.Facets)
            .Where(f => MarkerWorldCorners.HasFrame(f) && !claims.Values.Contains(f.Id))
            .Select(f => f.Id)
            .ToList();
        var renamed = FinalIds(root, claims, carried);
        RebaseClaimedFrames(root, reference, claims, renamed);
        RegroupSegments(root, referenceRoot, reference, claims.Values.ToHashSet(StringComparer.Ordinal));
        Carry(root, referenceRoot, reference, carried, new HashSet<int>());
        RefreshMarkerIds(root);

        ChildObject(root, "quality")["registration"] = new JsonObject
        {
            ["referenceModelId"] = referenceModelId.ToString(),
            ["method"] = "anchors+planes",
            ["claimedFacets"] = new JsonObject(claims.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["renamedFacets"] = new JsonObject(renamed.Where(kv => kv.Key != kv.Value).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
            ["carriedFacets"] = new JsonArray(carried.Select(f => (JsonNode?)f).ToArray()),
            ["facetFrames"] = "solved-rebased",
        };
        return new PlaneRegistrationResult(root.ToJsonString(), claims);
    }

    /// <summary>
    /// New facet id → reference facet id: pairs facing the same way, within <see cref="PlaneMatchMm"/> of the reference
    /// plane, whose outlines overlap in the reference plane; the biggest overlaps claim first, each facet once.
    /// </summary>
    private static Dictionary<string, string> MatchFacetsByPlane(WallGeometryDocument solved, WallGeometryDocument reference)
    {
        var pairs = new List<(string New, string Old, double Overlap)>();
        foreach (var facet in solved.Segments.SelectMany(s => s.Facets).Where(f => MarkerWorldCorners.HasFrame(f) && f.ExtentMm is not null))
        {
            var corners = Corners(facet);
            var centre = Enumerable.Range(0, 3).Select(i => corners.Average(c => c[i])).ToArray();
            foreach (var target in reference.Segments.SelectMany(s => s.Facets).Where(f => MarkerWorldCorners.HasFrame(f) && f.ExtentMm is not null))
            {
                var normal = target.Normal is { Length: 3 } n ? Unit(n) : Unit(Cross(target.U!, target.V!));
                var facing = facet.Normal is not { Length: 3 } || Vec3.AngleDeg(facet.Normal, normal) <= MatchNormalToleranceDeg;
                if (!facing || Math.Abs(Vec3.Dot(Vec3.Sub(centre, target.Origin!), normal)) > PlaneMatchMm)
                {
                    continue;
                }

                var projected = PlaneRectMm.Bounds(corners.Select(c => ToPlane(target, c)).Select(p => (p[0], p[1])));
                if (projected?.Intersect(target.ExtentMm!.Value) is { } overlap)
                {
                    pairs.Add((facet.Id, target.Id, overlap.Area));
                }
            }
        }

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, old, _) in pairs.OrderByDescending(p => p.Overlap).ThenBy(p => p.New, StringComparer.Ordinal))
        {
            if (!claims.ContainsKey(id) && !claims.ContainsValue(old))
            {
                claims[id] = old;
            }
        }

        return claims;
    }

    /// <summary>
    /// A claimed facet moves into (a copy of) its reference facet's segment, so the reference's segment index, name and
    /// declared angle stay with it; the others keep their own segment under a fresh index after the reference's.
    /// </summary>
    private static void RegroupSegments(JsonObject root, JsonObject referenceRoot, WallGeometryDocument reference, IReadOnlySet<string> claimedIds)
    {
        var referenceSegments = (referenceRoot["segments"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var next = referenceSegments.Select(s => s["index"]?.GetValue<int>() ?? 0).DefaultIfEmpty(-1).Max() + 1;
        var result = new List<JsonObject>();
        foreach (var segment in (root["segments"] as JsonArray ?? []).OfType<JsonObject>().ToList())
        {
            JsonObject? own = null;
            foreach (var facet in (segment["facets"] as JsonArray ?? []).OfType<JsonObject>().ToList())
            {
                var id = facet["id"]!.GetValue<string>();
                JsonObject target;
                if (claimedIds.Contains(id))
                {
                    target = ReferenceSegment(result, referenceSegments, reference.FindFacet(id)!.Value.Segment.Index);
                    target["measuredAngleDeg"] = facet["measuredAngleDeg"]?.DeepClone();
                }
                else
                {
                    own ??= EmptySegment(segment, next++, result);
                    target = own;
                }

                (target["facets"] as JsonArray)!.Add(facet.DeepClone());
            }
        }

        root["segments"] = new JsonArray(result.OrderBy(s => s["index"]!.GetValue<int>()).Select(s => (JsonNode?)s).ToArray());
    }

    private static JsonObject ReferenceSegment(List<JsonObject> result, List<JsonObject> referenceSegments, int index)
    {
        var existing = result.FirstOrDefault(s => s["index"]?.GetValue<int>() == index);
        if (existing is not null)
        {
            return existing;
        }

        var source = referenceSegments.First(s => s["index"]?.GetValue<int>() == index);
        return EmptySegment(source, index, result);
    }

    private static JsonObject EmptySegment(JsonObject source, int index, List<JsonObject> result)
    {
        var copy = source.DeepClone().AsObject();
        copy["index"] = index;
        copy["facets"] = new JsonArray();
        result.Add(copy);
        return copy;
    }
}
