// <copyright file="WallFrameRegistrationSolvedFacetsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// A registered re-solve keeps ITS geometry: a facet whose plane moved keeps the new plane (moved into the wall
/// frame) and the new measured angles under the old facet id, with a frame rebased so hold (a, b) positions stay.
/// </summary>
public class WallFrameRegistrationSolvedFacetsTests
{
    [Fact]
    public void AFacetThatMovedInTheReSolve_KeepsItsNewPlane_UnderTheOldId()
    {
        var tilted = Tilt(Rev1Json, "5", 0.4);
        var solved = Move(Rename(SetAngle(tilted, "5", 44.9), "5", "7"), Transform(3, 1, [250, -80, 40]));

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), null);
        var json = WallFrameRegistrationWriter.Rewrite(solved, Rev1Json, result, null, WallFrameRegistrationTests.Stamp());
        var rewritten = Doc(json);

        Assert.True(result.Accepted, result.Message);
        Assert.Null(rewritten.FindFacet("7"));
        var facet = rewritten.FindFacet("5")!.Value.Facet;
        var old = Doc(Rev1Json).FindFacet("5")!.Value.Facet;
        Assert.InRange(Angle(facet.Normal!, old.Normal!), 0.3, 0.5);
        Assert.InRange(Angle(facet.Normal!, Doc(tilted).FindFacet("5")!.Value.Facet.Normal!), 0, 0.05);
        Assert.Equal(44.9, facet.MeasuredAngleDeg);
        Assert.All(new[] { 31, 32, 33 }, id => Assert.Equal("5", rewritten.Markers.Single(m => m.Id == id).Facet));
        var before = PlaneCentres(Rev1Json);
        var after = PlaneCentres(json);
        Assert.All(new[] { 31, 32, 33 }, id => Assert.InRange(Distance(before[id], after[id]), 0, 10));
        Assert.Contains("\"facetFrames\":\"solved-rebased\"", json);
        Assert.Empty(WallGeometryValidator.Validate(rewritten));
    }

    [Fact]
    public void ABetterGravity_ChangesTheMeasuredAngles_AndTheUpDirection_NotTheFacetIds()
    {
        // The same physical wall, levelled 1.3° differently: registration undoes the tilt, the angles are the new ones.
        var solved = Move(SetAngle(Rev1Json, "0", 45.2), Transform(0, 1.3, [120, -60, 25]));

        var result = WallFrameRegistration.Register(Doc(Rev1Json), Doc(solved), null);
        var json = WallFrameRegistrationWriter.Rewrite(solved, Rev1Json, result, null, WallFrameRegistrationTests.Stamp());
        var rewritten = Doc(json);

        Assert.True(result.Accepted, result.Message);
        var facet = rewritten.FindFacet("0")!.Value.Facet;
        Assert.Equal(45.2, facet.MeasuredAngleDeg);
        Assert.InRange(Angle(facet.Normal!, Doc(Rev1Json).FindFacet("0")!.Value.Facet.Normal!), 0, 0.01);
        Assert.InRange(Angle(rewritten.World!.Up!, [0, 0, 1]), 1.29, 1.31);
        Assert.Equal(
            Doc(Rev1Json).Segments.SelectMany(s => s.Facets).Select(f => f.Id).Order(),
            rewritten.Segments.SelectMany(s => s.Facets).Select(f => f.Id).Order());
    }

    [Fact]
    public void RebasedFrame_IsOrthonormal_OnTheNewPlane_AndKeepsTheOldOrigin()
    {
        var old = new WallGeometryFacet { Id = "0", Origin = [0, 0, 0], U = [1, 0, 0], V = [0, 0, 1], Normal = [0, -1, 0] };
        var n = Unit([0, -1, 0.05]);
        var solved = new WallGeometryFacet { Origin = [300, 20, 100], U = Unit(Cross([0, 0, 1], n)), V = [0, 0, 1], Normal = n };

        var frame = WallFrameRegistrationWriter.Rebase(old, solved);

        Assert.Equal(n, frame.Normal);
        Assert.Equal(0, Dot(frame.U!, n), 9);
        Assert.Equal(0, Dot(frame.V!, n), 9);
        Assert.Equal(0, Dot(frame.U!, frame.V!), 9);
        Assert.Equal(0, Dot(Sub(frame.Origin!, solved.Origin!), n), 9);
        Assert.InRange(Math.Sqrt(Dot(Sub(frame.Origin!, old.Origin!), Sub(frame.Origin!, old.Origin!))), 0, 20);
        Assert.True(Dot(frame.U!, old.U!) > 0.999 && Dot(frame.V!, old.V!) > 0.998);
    }

    /// <summary>Tilts one facet by <paramref name="deg"/> about world x through its markers' centre (they follow: plane coordinates stay).</summary>
    private static string Tilt(string json, string facetId, double deg)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var facet = Facet(root, facetId);
        var r = Transform(0, deg, [0, 0, 0]);
        var corners = root["markers"]!.AsArray().OfType<JsonObject>()
            .Where(m => m["facet"]!.GetValue<string>() == facetId)
            .SelectMany(m => m["cornersPlaneMm"]!.AsArray().Select(c => Vec(c!)))
            .ToList();
        var (u, v, origin) = (Vec(facet["u"]!), Vec(facet["v"]!), Vec(facet["origin"]!));
        var (a, b) = (corners.Average(c => c[0]), corners.Average(c => c[1]));
        double[] centre = [origin[0] + (a * u[0]) + (b * v[0]), origin[1] + (a * u[1]) + (b * v[1]), origin[2] + (a * u[2]) + (b * v[2])];
        var turned = r.Rotate(Sub(origin, centre));
        facet["origin"] = Node([centre[0] + turned[0], centre[1] + turned[1], centre[2] + turned[2]]);
        facet["u"] = Node(r.Rotate(Vec(facet["u"]!)));
        facet["v"] = Node(r.Rotate(Vec(facet["v"]!)));
        facet["normal"] = Node(r.Rotate(Vec(facet["normal"]!)));
        return root.ToJsonString();
    }

    private static string SetAngle(string json, string facetId, double deg)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        Facet(root, facetId)["measuredAngleDeg"] = deg;
        return root.ToJsonString();
    }

    private static string Rename(string json, string from, string to)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        Facet(root, from)["id"] = to;
        foreach (var marker in root["markers"]!.AsArray().OfType<JsonObject>().Where(m => m["facet"]!.GetValue<string>() == from))
        {
            marker["facet"] = to;
        }

        return root.ToJsonString();
    }

    private static JsonObject Facet(JsonObject root, string id) =>
        root["segments"]!.AsArray().SelectMany(s => s!["facets"]!.AsArray()).OfType<JsonObject>().First(f => f["id"]!.GetValue<string>() == id);

    private static WallGeometryDocument Doc(string json) => WallGeometryDocument.Parse(json);

    private static double Distance((string Facet, double A, double B) a, (string Facet, double A, double B) b) =>
        a.Facet == b.Facet ? Math.Sqrt(Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2)) : double.PositiveInfinity;

    private static double Angle(double[] a, double[] b) =>
        Math.Acos(Math.Clamp(Dot(Unit(a), Unit(b)), -1, 1)) * 180 / Math.PI;

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];

    private static double[] Unit(double[] a)
    {
        var l = Math.Sqrt(Dot(a, a));
        return [a[0] / l, a[1] / l, a[2] / l];
    }

    private static double[] Vec(JsonNode node) => node.AsArray().Select(v => v!.GetValue<double>()).ToArray();

    private static JsonArray Node(double[] v) => new(v.Select(x => (JsonNode?)x).ToArray());
}
