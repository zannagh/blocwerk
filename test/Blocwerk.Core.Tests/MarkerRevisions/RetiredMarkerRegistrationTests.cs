// <copyright file="RetiredMarkerRegistrationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using static Blocwerk.Core.Tests.MarkerRevisions.RegistrationFixtures;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// An active model solved before id 17 was retired keeps 17's world corners. A new model has no 17 (its detections
/// never reach the solver), so the registration ties the two on the other unchanged markers, even when the plan
/// still lists 17 as unchanged, and the active model stays a valid reference.
/// </summary>
public class RetiredMarkerRegistrationTests
{
    [Fact]
    public void ANewModelWithout17_RegistersToAnActiveModelWith17_OnTheOtherMarkers()
    {
        var active = Renumber(Rev1Json, 14, 17);
        var solved = Remove(Move(active, Transform(4, 1.5, [320, -150, 60])), 17);
        var unchanged = Doc(active).Markers.Select(m => m.Id).ToHashSet();
        Assert.Contains(17, unchanged);
        Assert.Contains(17, MarkerWorldCorners.Of(Doc(active)).Keys);

        var result = WallFrameRegistration.Register(Doc(active), Doc(solved), unchanged);

        Assert.True(result.Accepted, result.Message);
        Assert.DoesNotContain(17, result.UsedIds);
        Assert.DoesNotContain(17, result.OutlierIds);
        Assert.True(result.RmsMm < 0.05, $"rms {result.RmsMm}");
        Assert.Empty(WallGeometrySanityGate.Problems(active));
    }

    private static WallGeometryDocument Doc(string json) => WallGeometryDocument.Parse(json);

    private static string Renumber(string json, int from, int to)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["markers"]!.AsArray().OfType<JsonObject>().First(m => m["id"]!.GetValue<int>() == from)["id"] = to;
        return root.ToJsonString();
    }

    private static string Remove(string json, int id)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var markers = root["markers"]!.AsArray();
        markers.Remove(markers.OfType<JsonObject>().First(m => m["id"]!.GetValue<int>() == id));
        return root.ToJsonString();
    }
}
