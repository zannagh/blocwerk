// <copyright file="BigUpdateHeicStagingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A HEIC panel photo is converted to an upright JPEG before it is staged, so hold detection and the
/// browser never see HEIC, and no orientation tag survives to turn the already-upright pixels again.
/// </summary>
public class BigUpdateHeicStagingTests
{
    private static readonly byte[] Heic = [0, 0, 0, 24, .. Encoding.ASCII.GetBytes("ftypheic"), 0, 0, 0, 0, .. Encoding.ASCII.GetBytes("mif1heic")];

    [Fact]
    public async Task HeicPhoto_IsStagedAsAStrippedJpeg_AndDetectionSeesTheJpeg()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        var converter = new FakeConverter(ExifJpeg.Build(CaptureScenario.TinyJpeg()));

        await Service(h, converter).StageAsync(h.WallId, [new BigUpdatePhoto(Heic, "image/heic", 0, 0)]);

        await using var db = h.CreateContext();
        var panel = await db.WallPanels.SingleAsync(p => p.StagedPhoto != null);
        Assert.Equal("image/jpeg", panel.StagedPhotoContentType);
        Assert.Equal(CapturePhotoKind.Jpeg, CapturePhotoFormat.Sniff(panel.StagedPhoto));
        Assert.Equal(-1, panel.StagedPhoto!.AsSpan().IndexOf(ExifJpeg.GpsLatitudeBytes));
        Assert.True(ExifOrientation.Read(panel.StagedPhoto) is null or 1);
        Assert.Same(Heic, converter.Received);
        await h.HoldDetection.Received().DetectHoldsAsync(
            Arg.Is<byte[]>(b => CapturePhotoFormat.Sniff(b) == CapturePhotoKind.Jpeg), Arg.Any<HoldDetectionParameters?>());
    }

    [Fact]
    public async Task HeicPhoto_WithoutAConverter_IsRefusedBeforeAnythingIsStaged()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(h, null).StageAsync(h.WallId, [new BigUpdatePhoto(Heic, "image/heic", 0, 0)]));

        await using var db = h.CreateContext();
        Assert.False(await db.WallPanels.AnyAsync(p => p.StagedPhoto != null));
    }

    private static WallBigUpdateService Service(WallTestHarness h, ICapturePhotoConverter? converter) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance,
            photoConverter: converter);

    private sealed class FakeConverter(byte[] jpeg) : ICapturePhotoConverter
    {
        public byte[]? Received { get; private set; }

        public Task<byte[]> ToJpegAsync(byte[] heic, CancellationToken ct)
        {
            Received = heic;
            return Task.FromResult(jpeg);
        }
    }
}
