// <copyright file="RegistrationFixtures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// The real 14-photo capture of The Attic, solved twice by the geometry worker
/// (<c>docker/wall-geometry</c>, request <c>tests/fixtures/capture1-request.json</c>): revision 1 as
/// captured (legacy ids, all photos) and "revision 2", in which the four spare fillers 24–27 were replaced
/// by NEW ids 44–47 (same physical sheets) and three photos are missing. Plus helpers to move a document
/// into another frame the way a solver's frame choice would.
/// </summary>
public static class RegistrationFixtures
{
    public static string Rev1Json { get; } = Read("capture1-rev1-geometry.json");

    public static string Rev2Json { get; } = Read("capture1-rev2-geometry.json");

    /// <summary>A rotation of <paramref name="zDeg"/> about z, then <paramref name="xDeg"/> about x, then a shift.</summary>
    public static RigidTransform3D Transform(double zDeg, double xDeg, double[] shift)
    {
        var (cz, sz) = (Math.Cos(zDeg * Math.PI / 180), Math.Sin(zDeg * Math.PI / 180));
        var (cx, sx) = (Math.Cos(xDeg * Math.PI / 180), Math.Sin(xDeg * Math.PI / 180));
        var rz = new double[,]
        {
            { cz, -sz, 0 },
            { sz, cz, 0 },
            { 0, 0, 1 },
        };
        var rx = new double[,]
        {
            { 1, 0, 0 },
            { 0, cx, -sx },
            { 0, sx, cx },
        };
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                r[i, j] = Enumerable.Range(0, 3).Sum(k => rx[i, k] * rz[k, j]);
            }
        }

        return RigidTransform3D.From(r, shift);
    }

    /// <summary>
    /// Moves every world quantity of <paramref name="json"/> by <paramref name="t"/> (facet frames and marker
    /// world corners; plane coordinates are frame-relative and stay), and slides facet
    /// <paramref name="shiftFacet"/>'s plane origin by <paramref name="shiftAMm"/> along its u — what a new
    /// marker at that facet's left edge does to the solver's bounding-box origin.
    /// </summary>
    public static string Move(string json, RigidTransform3D t, string? shiftFacet = null, double shiftAMm = 0)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var facet in root["segments"]!.AsArray().SelectMany(s => s!["facets"]!.AsArray()).OfType<JsonObject>())
        {
            var u = Vec(facet["u"]!);
            var origin = Vec(facet["origin"]!);
            if (facet["id"]!.GetValue<string>() == shiftFacet)
            {
                origin = [origin[0] - (shiftAMm * u[0]), origin[1] - (shiftAMm * u[1]), origin[2] - (shiftAMm * u[2])];
                ShiftPlane(root, shiftFacet, shiftAMm);
            }

            facet["origin"] = Node(t.Apply(origin));
            facet["u"] = Node(t.Rotate(u));
            facet["v"] = Node(t.Rotate(Vec(facet["v"]!)));
            facet["normal"] = Node(t.Rotate(Vec(facet["normal"]!)));
        }

        foreach (var marker in root["markers"]!.AsArray().OfType<JsonObject>())
        {
            if (marker["cornersWorldMm"] is JsonArray corners)
            {
                marker["cornersWorldMm"] = new JsonArray(corners.Select(c => (JsonNode?)Node(t.Apply(Vec(c!)))).ToArray());
            }
        }

        return root.ToJsonString();
    }

    /// <summary>Slides marker <paramref name="id"/> by (da, db) mm in its plane and resizes it about its centre.</summary>
    public static string Displace(string json, int id, double da, double db, double scale = 1)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var marker = root["markers"]!.AsArray().OfType<JsonObject>().First(m => m["id"]!.GetValue<int>() == id);
        var corners = marker["cornersPlaneMm"]!.AsArray().Select(c => Vec(c!)).ToList();
        var (ca, cb) = (corners.Average(c => c[0]), corners.Average(c => c[1]));
        marker["cornersPlaneMm"] = new JsonArray(corners
            .Select(c => (JsonNode?)Node([ca + ((c[0] - ca) * scale) + da, cb + ((c[1] - cb) * scale) + db]))
            .ToArray());
        return root.ToJsonString();
    }

    /// <summary>Removes a segment and its markers, as if it had not been photographed.</summary>
    public static string WithoutSegment(string json, int index)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var segments = root["segments"]!.AsArray();
        var segment = segments.OfType<JsonObject>().First(s => s["index"]!.GetValue<int>() == index);
        var facets = segment["facets"]!.AsArray().Select(f => f!["id"]!.GetValue<string>()).ToHashSet();
        segments.Remove(segment);
        var markers = root["markers"]!.AsArray();
        foreach (var marker in markers.OfType<JsonObject>().Where(m => facets.Contains(m["facet"]!.GetValue<string>())).ToList())
        {
            markers.Remove(marker);
        }

        return root.ToJsonString();
    }

    /// <summary>Marker id → centre in its facet's plane.</summary>
    public static Dictionary<int, (string Facet, double A, double B)> PlaneCentres(string json) =>
        WallGeometryDocument.Parse(json).Markers.ToDictionary(
            m => m.Id, m => (m.Facet, m.CornersPlaneMm.Average(c => c[0]), m.CornersPlaneMm.Average(c => c[1])));

    private static void ShiftPlane(JsonObject root, string facet, double shiftAMm)
    {
        foreach (var marker in root["markers"]!.AsArray().OfType<JsonObject>().Where(m => m["facet"]!.GetValue<string>() == facet))
        {
            var corners = marker["cornersPlaneMm"]!.AsArray().Select(c => Vec(c!)).ToList();
            marker["cornersPlaneMm"] = new JsonArray(corners.Select(c => (JsonNode?)Node([c[0] + shiftAMm, c[1]])).ToArray());
        }
    }

    private static double[] Vec(JsonNode node) => node.AsArray().Select(v => v!.GetValue<double>()).ToArray();

    private static JsonArray Node(double[] v) => new(v.Select(x => (JsonNode?)x).ToArray());

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MarkerRevisions", name));
}
