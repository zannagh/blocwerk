// <copyright file="ResolveRegistrationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Registration;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>
/// Real re-solves of ONE physical, unchanged wall (The Attic's 14-photo capture, request
/// <c>docker/wall-geometry/tests/fixtures/capture1-request.json</c>, plan id scheme) registered onto the full
/// solve: the first 11 or 8 photos, the spares 24–27 left out of the solve (a plan that no longer lists
/// them), and — the E2E's mistake — fillers 4/5 declared 100 mm while the printed sheets are 125 mm. The
/// honest ones must be accepted within the solver's own repeatability; the misdeclared ones distort the
/// whole solve and must be refused with a reason that names the markers the solver could not fit — also the
/// E2E's own 11-photo model, whose fit alone (12.9 mm) would pass.
/// </summary>
public class ResolveRegistrationTests(ITestOutputHelper output)
{
    private static readonly IReadOnlySet<int> NotFillers =
        Enumerable.Range(0, 50).Where(id => id is not (4 or 5)).ToHashSet();

    [Fact]
    public void TheSameSolve_RegistersExactly()
    {
        var result = WallFrameRegistration.Register(Load("attic-14"), Load("attic-14"), null);

        Assert.True(result.Accepted, result.Message);
        Assert.True(result.RmsMm < 0.01, $"rms {result.RmsMm}");
        Assert.True(result.Transform!.TranslationMm < 0.01);
    }

    [Theory]
    [InlineData("attic-11")]
    [InlineData("attic-8")]
    [InlineData("attic-14-without-24-27")]
    [InlineData("attic-8-without-24-27")]
    public void ResolvesOfTheUnchangedWall_AreAccepted_WithinTheSolversRepeatability(string name)
    {
        var result = WallFrameRegistration.Register(Load("attic-14"), Load(name), null);

        Log(name, result);
        Assert.True(result.Accepted, result.Message);
        Assert.True(result.RmsMm < 13, $"rms {result.RmsMm}");
        Assert.True(result.OutlierIds.Count <= 1, $"outliers {string.Join(",", result.OutlierIds)}");
    }

    [Fact]
    public void WellSeenMarkers_WeighMore_ThanOnesInOnePhoto()
    {
        // 14 vs 8 photos: marker 0 is in one photo of the 8-photo solve and misses by ~17 mm; the weighted
        // RMS rests on the markers both solves saw several times.
        var result = WallFrameRegistration.Register(Load("attic-14"), Load("attic-8"), null);
        var plain = Math.Sqrt(result.ResidualsMm!.Values.Average(r => r * r));

        Assert.True(result.RmsMm < plain, $"weighted {result.RmsMm}, plain {plain}");
        Assert.Equal(0, result.ResidualsMm.MaxBy(kv => kv.Value).Key);
    }

    [Theory]
    [InlineData("attic-11-fillers-misdeclared")]
    [InlineData("attic-8-fillers-misdeclared")]
    public void ASolveDistortedByMisdeclaredSizes_IsRefused_NamingTheMarkers(string name)
    {
        var result = WallFrameRegistration.Register(Load("attic-14"), Load(name), NotFillers);

        Log(name, result);
        Assert.False(result.Accepted);
        Assert.Null(result.Transform);

        // the solver measured the sheet at its printed size: the refusal names both sizes
        Assert.Matches(@"Marker 5 was planned at 100 mm but measures ≈12[3-7] mm in the photos; check its printed size\.", result.Message);
    }

    [Fact]
    public void TheE2EsElevenPhotoSolve_WithFillersMisdeclared_IsRefused_ThoughItsFitLooksAcceptable()
    {
        // The real E2E models: revision 1 from all 14 photos, revision 2 (fillers 4/5 declared 100 mm, printed
        // 125 mm) from 11. Its weighted fit is 12.9 mm, under the limit; the solver's own flags on the changed
        // markers must refuse it anyway.
        var result = WallFrameRegistration.Register(Load("e2e-rev1-14"), Load("e2e-rev2-11-fillers-misdeclared"), NotFillers);

        Log("e2e-rev2-11-fillers-misdeclared", result);
        Assert.False(result.Accepted);
        Assert.Null(result.Transform);
        Assert.Contains("Marker 4 was planned at 100 mm but does not fit the photos as 100 mm", result.Message);
        Assert.Contains("Marker 5 was planned at 100 mm", result.Message);
        Assert.Contains("check its printed size", result.Message);
    }

    [Fact]
    public void ManyFlaggedUnchangedMarkers_AreRefused()
    {
        var json = JsonNode.Parse(File.ReadAllText(Path("attic-11")))!.AsObject();
        var flags = new JsonObject();
        foreach (var id in new[] { 2, 6, 7, 10, 14 })
        {
            flags[id.ToString()] = new JsonObject { ["freeRmsPx"] = 9.0, ["medianRmsPx"] = 1.5 };
        }

        json["quality"]!["downweightedMarkers"] = flags;
        var result = WallFrameRegistration.Register(Load("attic-14"), WallGeometryDocument.Parse(json.ToJsonString()), null);

        Assert.False(result.Accepted);
        Assert.Contains("could not fit 5 of the", result.Message);
    }

    [Fact]
    public void APoorSolve_SaysSo()
    {
        // Every shared marker counted as unchanged (so the fillers' flags don't pre-empt the residual check).
        var result = WallFrameRegistration.Register(Load("attic-14"), Load("attic-8-fillers-misdeclared"), null);

        Assert.False(result.Accepted);
        Assert.Contains("px reprojection error", result.Message);
        Assert.Contains("Worst: marker", result.Message);
        Assert.Contains("could not fit marker(s) 5, 32", result.Message);
        Assert.True(result.RmsMm > 30, $"rms {result.RmsMm}");
    }

    private void Log(string name, FrameRegistrationResult result)
    {
        var worst = string.Join(" ", (result.ResidualsMm ?? new Dictionary<int, double>())
            .OrderByDescending(kv => kv.Value).Take(5).Select(kv => $"{kv.Key}:{kv.Value:0.0}"));
        output.WriteLine($"{name}: accepted {result.Accepted}, rms {result.RmsMm:F1} mm, max {result.MaxMm:F1} mm, "
                         + $"outliers [{string.Join(",", result.OutlierIds)}], worst {worst}");
        if (result.Message is not null)
        {
            output.WriteLine(result.Message);
        }
    }

    private static WallGeometryDocument Load(string name) => WallGeometryDocument.Parse(File.ReadAllText(Path(name)));

    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "MarkerRevisions", "Resolves", name + ".json");
}
