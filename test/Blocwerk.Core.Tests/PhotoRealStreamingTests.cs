// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;

namespace Blocwerk.Core.Tests;

/// <summary>The stored level-of-detail ladder and the photo-real diagnostics admission.</summary>
public class PhotoRealStreamingTests
{
    [Fact]
    public void Ladder_RoundTrips_Sorted_AndSurvivesJunk()
    {
        var json = SplatLodLadder.Serialize([new(120_000, "b.spz", 2), new(40_000, "a.spz", 1)]);

        Assert.Equal([40_000, 120_000], SplatLodLadder.Parse(json).Select(l => l.Splats));
        Assert.Empty(SplatLodLadder.Parse(null));
        Assert.Empty(SplatLodLadder.Parse("{not json"));
        Assert.Empty(SplatLodLadder.Parse("[{\"splats\":0,\"storedPath\":\"x.spz\"}]"));
    }

    [Fact]
    public void Select_PicksTheLevel_TheLegacyMobileCopy_OrTheFullScene()
    {
        var splat = new WallGeometrySplat
        {
            StoredPath = "full.spz", SizeBytes = 9, MobileStoredPath = "m.spz", MobileSizeBytes = 5, FrameJson = "{}",
            LodLevelsJson = SplatLodLadder.Serialize([new(40_000, "a.spz", 1)]), UncleanedStoredPath = "raw.spz",
        };

        Assert.Equal(("a.spz", 1L, "geometry-splat-lod40000"), WallGeometrySplats.Select(splat, "40000"));
        Assert.Equal(("m.spz", 5L, "geometry-splat-mobile"), WallGeometrySplats.Select(splat, "mobile"));
        Assert.Equal("full.spz", WallGeometrySplats.Select(splat, "120000").Path);
        Assert.Equal("full.spz", WallGeometrySplats.Select(splat, "-1").Path);
        Assert.Equal("full.spz", WallGeometrySplats.Select(splat, null).Path);
        Assert.Equal(["full.spz", "m.spz", "a.spz", "raw.spz"], SplatLodLadder.Files(splat));
    }

    [Fact]
    public void Diagnostics_AreRateLimitedPerUser_InASlidingWindow()
    {
        var key = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < PhotoRealDiagnosticsEndpoints.MaxReports; i++)
        {
            Assert.True(PhotoRealDiagnosticsEndpoints.Admit(key, now));
        }

        Assert.False(PhotoRealDiagnosticsEndpoints.Admit(key, now));
        Assert.True(PhotoRealDiagnosticsEndpoints.Admit(Guid.NewGuid().ToString(), now));
        Assert.True(PhotoRealDiagnosticsEndpoints.Admit(key, now + PhotoRealDiagnosticsEndpoints.Window + TimeSpan.FromSeconds(1)));
    }
}
