// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>The manifest v1 validator: a valid package passes, and each kind of fault is named.</summary>
public class ScanManifestValidatorTests
{
    private static readonly Guid WallId = Guid.NewGuid();

    [Fact]
    public void AValidPackage_HasNoProblems()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        Assert.Empty(ScanManifestValidator.Validate(manifest, WallId));
    }

    [Fact]
    public void AnUnknownSchemaVersion_IsTheOnlyProblemReported()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        var problems = ScanManifestValidator.Validate(manifest with { SchemaVersion = "blocwerk-scan/2", Frames = null }, WallId);

        Assert.Contains("blocwerk-scan/2", Assert.Single(problems));
    }

    [Fact]
    public void AnotherWallsManifest_IsRefused()
    {
        var (manifest, _) = ScanPackageBuilder.Build(Guid.NewGuid());

        Assert.Contains(ScanManifestValidator.Validate(manifest, WallId), p => p.StartsWith("wallId", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileUsedButNotListed_AndAFileListedButNotUsed_AreNamed()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var files = manifest.Files!.Where(f => f.Name != "mesh/arkit.ply")
            .Append(new ScanFileEntry("video/extra.mov", 10, new string('a', 64)))
            .ToList();

        var problems = ScanManifestValidator.Validate(manifest with { Files = files }, WallId);

        Assert.Contains(problems, p => p.Contains("mesh/arkit.ply, which files does not list", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("video/extra.mov, but nothing in the manifest uses it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("video/../../x.mov")]
    [InlineData("keyframes/k1.heic")]
    [InlineData("depth/d1.mov")]
    [InlineData("mesh/arkit.ply\n")]
    [InlineData("video/seg0-000.mov\r\n")]
    public void APathThatIsNoPackageName_IsRefused(string name)
    {
        Assert.Equal(ScanFileKind.Invalid, ScanFileNames.KindOf(name));
    }

    [Fact]
    public void FileSizesAndHashes_AreChecked()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var files = manifest.Files!.Select(f => f.Name switch
        {
            "video/seg0-000.mov" => f with { Bytes = ScanPackageLimits.MaxVideoFileBytes + 1 },
            "mesh/arkit.ply" => f with { Sha256 = "XYZ" },
            _ => f,
        }).ToList();

        var problems = ScanManifestValidator.Validate(manifest with { Files = files }, WallId);

        Assert.Contains(problems, p => p.StartsWith("video/seg0-000.mov: bytes", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("mesh/arkit.ply: sha256", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyFourKHevc_IsTaken()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);

        var problems = ScanManifestValidator.Validate(manifest with { Video = new ScanVideoFormat("h264", 1920, 1080, 30) }, WallId);

        Assert.Contains(problems, p => p.Contains("hevc", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("4K", StringComparison.Ordinal));
    }

    [Fact]
    public void ARowMajorPose_AndImpossibleIntrinsics_AreNamedPerFrame()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        double[] rowMajor = [1, 0, 0, 0.5, 0, 1, 0, 1.5, 0, 0, 1, 2, 0, 0, 0, 1];
        var frames = manifest.Frames!.ToList();
        frames[2] = frames[2] with { Pose = rowMajor };
        frames[3] = frames[3] with { Intrinsics = new ScanIntrinsics(100, 100, 10, 10) };

        var problems = ScanManifestValidator.Validate(manifest with { Frames = frames }, WallId);

        Assert.Contains(problems, p => p.StartsWith("frames[2].pose: the bottom row", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("frames[3].intrinsics", StringComparison.Ordinal));
    }

    [Fact]
    public void AScaledRotation_IsNotRigid()
    {
        Assert.NotNull(ScanPoseCheck.Problem([2, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]));
        Assert.NotNull(ScanPoseCheck.Problem([-1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]));
        Assert.Null(ScanPoseCheck.Problem(ScanPackageBuilder.Pose(1)));
    }

    [Fact]
    public void FramesOutsideTheirSegment_OrGoingBack_AreNamed()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId);
        var frames = manifest.Frames!.ToList();
        frames[4] = frames[4] with { T = frames[1].T };
        frames[5] = frames[5] with { Segment = 7 };

        var problems = ScanManifestValidator.Validate(manifest with { Frames = frames }, WallId);

        Assert.Contains(problems, p => p.StartsWith("frames[4]: t", StringComparison.Ordinal) && p.Contains("does not increase", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("frames[5]: t", StringComparison.Ordinal) && p.Contains("segment 7", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPartsAndCounts_AreNamed()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId, frames: 2, depth: 2);

        var problems = ScanManifestValidator.Validate(manifest with { ExposureLock = null, WallBox = [], Frames = [] }, WallId);

        Assert.Contains(problems, p => p.StartsWith("exposureLock is missing", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("wallBox", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("frames must hold", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("depth must hold", StringComparison.Ordinal));
    }

    [Fact]
    public void ThousandsOfBadFrames_AnswerWithACappedList()
    {
        var (manifest, _) = ScanPackageBuilder.Build(WallId, frames: 200);
        var frames = manifest.Frames!.Select(f => f with { Tracking = "lost" }).ToList();

        var problems = ScanManifestValidator.Validate(manifest with { Frames = frames }, WallId);

        Assert.Equal(ScanManifestErrors.Cap + 1, problems.Count);
        Assert.StartsWith("…", problems[^1], StringComparison.Ordinal);
    }
}
