using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Deterministic stand-ins for the OpenCV outliner and marker detector, on a virtual 1000 × 1000 px
/// photo. Seeds below y = 0.6 get a 40 × 60 px rectangle contour; seeds at or below get a circle fallback.
/// </summary>
internal static class EnrichmentFakes
{
    public const int ImageSize = 1000;

    public static IHoldOutlineService Outlines()
    {
        var session = Substitute.For<IHoldOutlineSession>();
        session.ImageWidth.Returns(ImageSize);
        session.ImageHeight.Returns(ImageSize);
        session.Outline(Arg.Any<HoldSeed>()).Returns(ci => Outline(ci.Arg<HoldSeed>()));
        var service = Substitute.For<IHoldOutlineService>();
        service.OpenSession(Arg.Any<byte[]>()).Returns(session);
        return service;
    }

    public static IHoldOutlineService ThrowingOutlines()
    {
        var service = Substitute.For<IHoldOutlineService>();
        service.OpenSession(Arg.Any<byte[]>()).Returns(_ => throw new InvalidOperationException("decoder exploded"));
        return service;
    }

    /// <summary>A detector that always "sees" <paramref name="markers"/>.</summary>
    public static IMarkerDetectionService Markers(params DetectedMarker[] markers)
    {
        var service = Substitute.For<IMarkerDetectionService>();
        service.DetectAsync(Arg.Any<byte[]>(), Arg.Any<MarkerDetectionOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new MarkerDetectionResult
            {
                ImageWidth = ImageSize,
                ImageHeight = ImageSize,
                Markers = markers,
                Rejected = [],
            }));
        return service;
    }

    /// <summary>An axis-aligned square marker seen head-on, TL at (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public static DetectedMarker Square(int id, double left, double top, double side)
    {
        MarkerPoint[] px = [new(left, top), new(left + side, top), new(left + side, top + side), new(left, top + side)];
        return new DetectedMarker
        {
            Id = id,
            CornersPx = px,
            CornersNormalized = px.Select(p => new MarkerPoint(p.X / ImageSize, p.Y / ImageSize)).ToArray(),
            SidePx = side,
            EdgeRatio = 1,
        };
    }

    public static HoldEnrichmentService Service(
        IHoldOutlineService? outlines, IMarkerDetectionService? markers, bool outlinesOn = true, bool markersOn = true)
    {
        var settings = new BlocwerkSettings();
        settings.HoldDetection.OutlinesEnabled = outlinesOn;
        settings.HoldDetection.MarkersEnabled = markersOn;
        return new HoldEnrichmentService(settings, NullLogger<HoldEnrichmentService>.Instance, outlines, markers);
    }

    public static Hold AutoHold(Guid wallId, double x, double y, double radius = 0.02) => new()
    {
        WallId = wallId,
        X = x,
        Y = y,
        Radius = radius,
        IsAutoDetected = true,
    };

    private static HoldOutlineResult Outline(HoldSeed seed)
    {
        var fingerprint = new HoldFingerprint { L = 120, A = 150, B = 140, AreaPx = 2400 };
        if (seed.Y >= 0.6)
        {
            return new HoldOutlineResult(
                [], seed.X, seed.Y, null, 0, default, 0.1, HoldOutlineMethod.CircleFallback, fingerprint);
        }

        NormalizedPoint[] polygon =
        [
            new(seed.X - 0.02, seed.Y - 0.03), new(seed.X + 0.02, seed.Y - 0.03),
            new(seed.X + 0.02, seed.Y + 0.03), new(seed.X - 0.02, seed.Y + 0.03),
        ];
        return new HoldOutlineResult(
            polygon,
            seed.X,
            seed.Y,
            HoldOutlineGeometry.ToShapePoints(polygon, seed.X, seed.Y),
            2400,
            new HoldOutlineBounds(seed.X - 0.02, seed.Y - 0.03, 0.04, 0.06),
            0.9,
            HoldOutlineMethod.Contour,
            fingerprint);
    }
}
