// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The splat worker's floater clean-up: the cleaned scene is the photo-real view, and the scene as
/// trained (<c>wall.raw.spz</c>) is kept next to it so the clean-up can be reverted.
/// </summary>
public class WallCaptureSplatCleanupTests
{
    [Fact]
    public async Task CleanedScene_IsStored_WithTheUncleanedCopyNextToIt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.FrameJson = CleanedFrame(applied: true);
        var raw = FakeComputeJobClient.Gzip(new byte[8192]);
        s.SplatClient.Download = name => name switch
        {
            "frame.json" => System.Text.Encoding.UTF8.GetBytes(s.SplatClient.FrameJson),
            "wall.spz" => s.SplatClient.Spz,
            "wall.raw.spz" => raw,
            _ => CaptureScenario.TinyJpeg(),
        };
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var splat = await db.WallGeometrySplats.SingleAsync();
        Assert.Equal(s.SplatClient.Spz, await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(splat.StoredPath)!));
        Assert.NotNull(splat.UncleanedStoredPath);
        Assert.Equal(raw, await File.ReadAllBytesAsync(s.Files.ResolvePhysicalPath(splat.UncleanedStoredPath)!));
        Assert.Equal(raw.LongLength, splat.UncleanedSizeBytes);
        Assert.Contains(splat.UncleanedStoredPath, SplatLodLadder.Files(splat));
    }

    [Fact]
    public async Task UncleanedScene_HasNoCopy_AndIsNotAskedFor()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.SplatClient.FrameJson = CleanedFrame(applied: false);
        var captureId = await s.StartCaptureAsync();

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        await using var db = h.CreateContext();
        var splat = await db.WallGeometrySplats.SingleAsync();
        Assert.Null(splat.UncleanedStoredPath);
        Assert.Null(splat.UncleanedSizeBytes);
        Assert.DoesNotContain("wall.raw.spz", s.SplatClient.Downloads);
    }

    [Theory]
    [InlineData(true, "wall.raw.spz", "wall.raw.spz")]
    [InlineData(false, "wall.raw.spz", null)]
    [InlineData(true, "../wall.raw.spz", null)]
    [InlineData(true, "sub/wall.raw.spz", null)]
    [InlineData(true, "wall.raw.ply", null)]
    public void UncleanedFile_IsABareSpzName_OnlyWhenTheCleanupWasApplied(bool applied, string rawFile, string? expected)
    {
        Assert.Equal(expected, CaptureSplatDocuments.UncleanedFile(CleanedFrame(applied, rawFile)));
        Assert.Null(CaptureSplatDocuments.UncleanedFile(FakeComputeJobClient.SplatFrame(aligned: true)));
        Assert.Null(CaptureSplatDocuments.UncleanedFile("not json"));
    }

    private static string CleanedFrame(bool applied, string rawFile = "wall.raw.spz")
    {
        var frame = JsonNode.Parse(FakeComputeJobClient.SplatFrame(aligned: true))!.AsObject();
        frame[CaptureSplatDocuments.CleanupField] = new JsonObject
        {
            ["applied"] = applied,
            ["rawFile"] = rawFile,
            ["removed"] = new JsonObject { ["aboveWallTop"] = 3, ["sparse"] = 5 },
        };
        return frame.ToJsonString();
    }
}
