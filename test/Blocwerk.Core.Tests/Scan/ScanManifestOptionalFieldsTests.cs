// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Capture.Scan;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// The optional parts of a v1 manifest: wallId, per-segment exposure locks, per-frame camera settings, the wall box
/// reach, coverage, the synthetic flag, and video sizes other than 4K. A manifest without any of them stays valid.
/// </summary>
public class ScanManifestOptionalFieldsTests
{
    private static readonly Guid WallId = Guid.NewGuid();

    [Fact]
    public void WithoutAWallId_TheManifestIsValid_AWrongOneIsNot()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        Assert.Empty(ScanManifestValidator.Validate(manifest with { WallId = null }, WallId));
        Assert.Contains(ScanManifestValidator.Validate(manifest with { WallId = Guid.NewGuid() }, WallId), p => p.StartsWith("wallId", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAWallBox_TheManifestIsValid_AnEmptyOneIsNot()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        Assert.Empty(ScanManifestValidator.Validate(manifest with { WallBox = null }, WallId));
        Assert.Contains(ScanManifestValidator.Validate(manifest with { WallBox = [] }, WallId), p => p.StartsWith("wallBox", StringComparison.Ordinal));
    }

    [Fact]
    public void AReferencePlane_IsTakenWithItsNormalNormalised_AZeroOrNonFiniteOneIsNot()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var plane = new ScanReferencePlane([0.5, 1.2, -1.5], [0, 0, 2]);

        Assert.Empty(ScanManifestValidator.Validate(manifest with { WallBox = null, ReferencePlane = plane }, WallId));
        Assert.Equal(new[] { 0.0, 0.0, 1.0 }, plane.UnitNormal);
        Assert.Contains(
            ScanManifestValidator.Validate(manifest with { ReferencePlane = plane with { Normal = [0, 0, 0] } }, WallId),
            p => p.StartsWith("referencePlane.normal", StringComparison.Ordinal));
        Assert.Contains(
            ScanManifestValidator.Validate(manifest with { ReferencePlane = plane with { Point = [double.NaN, 0, 0] } }, WallId),
            p => p.StartsWith("referencePlane.point", StringComparison.Ordinal));
        var read = JsonSerializer.Deserialize<ScanManifest>("""{"referencePlane":{"point":[1,2,3],"normal":[0,3,4]}}""", CapturePackageFiles.Json)!;
        Assert.Equal(new[] { 0.0, 0.6, 0.8 }, read.ReferencePlane!.UnitNormal);
    }

    [Fact]
    public void EveryOptionalPart_WhenValid_IsTaken()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var full = manifest with
        {
            Synthetic = true,
            WallBoxReach = new ScanWallBoxReach(1.5, 0.2),
            Coverage = new ScanCoverage(87.5, 0.25, new Dictionary<string, int> { ["frontal"] = 120, ["lookingUp"] = 0, ["fromBehindTheBar"] = 3 }),
            Frames = manifest.Frames!.Select(f => f with { Camera = new ScanFrameCamera(0.8, 200, 1.0 / 120, [1.9, 1.0, 2.1]) }).ToList(),
        };

        Assert.Empty(ScanManifestValidator.Validate(full, WallId));
    }

    [Fact]
    public void PerSegmentLocks_ReplaceTheRecordingsLock_ButOneOfThemIsNeeded()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var locked = manifest.ExposureLock! with { ExposureLocked = true, WhiteBalanceLocked = true, FocusLocked = false };
        var perSegment = manifest with { ExposureLock = null, Segments = manifest.Segments!.Select(s => s with { ExposureLock = locked }).ToList() };

        Assert.Empty(ScanManifestValidator.Validate(perSegment, WallId));
        Assert.Contains(
            ScanManifestValidator.Validate(manifest with { ExposureLock = null }, WallId),
            p => p.StartsWith("exposureLock is missing", StringComparison.Ordinal));
        var bad = perSegment with { Segments = perSegment.Segments!.Select(s => s with { ExposureLock = locked with { Iso = 0 } }).ToList() };
        Assert.Contains(ScanManifestValidator.Validate(bad, WallId), p => p.StartsWith("segment 0 exposureLock", StringComparison.Ordinal));
    }

    [Fact]
    public void BadOptionalParts_AreNamed()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var bad = manifest with
        {
            WallBoxReach = new ScanWallBoxReach(-1, 0),
            Coverage = new ScanCoverage(101, 0.25, new Dictionary<string, int> { ["frontal"] = -1 }),
            Frames = manifest.Frames!.Select((f, i) => i == 0 ? f with { Camera = new ScanFrameCamera(0.8, 200, 0.01, [1.9, 1.0]) } : f).ToList(),
        };

        var problems = ScanManifestValidator.Validate(bad, WallId);

        Assert.Contains(problems, p => p.StartsWith("wallBoxReach", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("coverage.percentGreen", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("coverage.bins", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("frames[0].camera: whiteBalanceGains", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1440)]
    [InlineData(2880, 2160)]
    [InlineData(1280, 960)]
    public void A16By9Or4By3Video_InTheRange_IsTaken_WithIntrinsicsInItsPixels(int width, int height)
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var scaled = manifest with
        {
            Video = new ScanVideoFormat("hevc", width, height, 30),
            Frames = manifest.Frames!.Select(f => f with { Intrinsics = new ScanIntrinsics(0.73 * width, 0.73 * width, width / 2.0, height / 2.0) }).ToList(),
        };

        Assert.Empty(ScanManifestValidator.Validate(scaled, WallId));
    }

    [Theory]
    [InlineData(4032, 3024)]
    [InlineData(1024, 576)]
    [InlineData(1920, 1200)]
    [InlineData(2160, 3840)]
    public void AVideoOutsideTheRangeOrAspect_IsRefused(int width, int height)
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        var problems = ScanManifestValidator.Validate(manifest with { Video = new ScanVideoFormat("hevc", width, height, 30) }, WallId);

        Assert.Contains(problems, p => p.StartsWith("video must be 16:9 or 4:3", StringComparison.Ordinal));
    }

    [Fact]
    public void IntrinsicsForAnotherSize_AreNamed()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        var problems = ScanManifestValidator.Validate(manifest with { Video = new ScanVideoFormat("hevc", 1280, 720, 30) }, WallId);

        Assert.Contains(problems, p => p.StartsWith("frames[0].intrinsics", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAppsJson_ReadsIntoTheManifest()
    {
        const string json = """
            {"wallBoxReach":{"frontMeters":1.2,"backMeters":0},"synthetic":true,
             "coverage":{"percentGreen":92,"patchSizeMeters":0.3,"bins":{"frontal":40,"fromLeft":12,"somethingNew":1}},
             "segments":[{"id":0,"exposureLock":{"iso":200,"exposureLocked":true,"whiteBalanceLocked":false,"focusLocked":true}}],
             "frames":[{"t":1,"camera":{"lensPosition":0.8,"iso":200,"exposureDurationSeconds":0.008,"whiteBalanceGains":[1.9,1,2.1]}}]}
            """;

        var m = JsonSerializer.Deserialize<ScanManifest>(json, CapturePackageFiles.Json)!;

        Assert.Equal((1.2, 0.0), (m.WallBoxReach!.FrontMeters!.Value, m.WallBoxReach.BackMeters!.Value));
        Assert.True(m.Synthetic);
        Assert.Equal(12, m.Coverage!.Bins!["fromLeft"]);
        Assert.Equal((true, false, true), (m.Segments![0].ExposureLock!.ExposureLocked, m.Segments[0].ExposureLock!.WhiteBalanceLocked, m.Segments[0].ExposureLock!.FocusLocked));
        Assert.Equal(new[] { 1.9, 1, 2.1 }, m.Frames![0].Camera!.WhiteBalanceGains!);
    }
}
