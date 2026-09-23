// <copyright file="MarkerQuietZoneCheckTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>
/// The quiet-zone check on The Attic's 53-photo capture: IMG_2803 decoded id 17 on a black hold (the
/// only false detection; it bent the left triangle's plane by 267 mm in the solve). Crops of that photo,
/// with the refined corners the detector produced, shifted into the crop.
/// </summary>
public class MarkerQuietZoneCheckTests
{
    private static readonly string GlyphDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs");

    private static readonly MarkerPoint[] False17 = [new(119, 211), new(125.08, 124.95), new(176.72, 123.07), new(210, 196)];

    private static readonly MarkerPoint[] Real12 = [new(136.25, 124.31), new(309.96, 178.85), new(273.28, 434.83), new(91.30, 403.96)];

    [Fact]
    public void AFalseIdOnABlackHold_HasNoQuietZone()
    {
        using var gray = Cv2.ImRead(Path.Combine(GlyphDir, "img2803-false17-hold.png"), ImreadModes.Grayscale);

        var contrast = MarkerQuietZoneCheck.Contrast(gray, False17);

        Assert.NotNull(contrast);
        Assert.True(contrast < MarkerQuietZoneCheck.MinContrast, $"contrast {contrast}");
        var (kept, rejected) = MarkerQuietZoneCheck.Filter(gray, [Marker(17, False17)]);
        Assert.Empty(kept);
        Assert.Equal(MarkerRejectionReason.NoQuietZone, Assert.Single(rejected).Reason);
    }

    [Fact]
    public void ARealMarkerInTheSamePhoto_IsKept()
    {
        using var gray = Cv2.ImRead(Path.Combine(GlyphDir, "img2803-marker12.png"), ImreadModes.Grayscale);

        var contrast = MarkerQuietZoneCheck.Contrast(gray, Real12);

        Assert.True(contrast > 0.3, $"contrast {contrast}");
        Assert.Single(MarkerQuietZoneCheck.Filter(gray, [Marker(12, Real12)]).Kept);
    }

    [Fact]
    public void AMarkerMostlyOutsideTheFrame_IsNotJudged()
    {
        using var gray = new Mat(new Size(200, 200), MatType.CV_8UC1, Scalar.All(0));
        MarkerPoint[] offFrame = [new(150, 20), new(260, 20), new(260, 130), new(150, 130)];

        Assert.Null(MarkerQuietZoneCheck.Contrast(gray, offFrame));
    }

    private static DetectedMarker Marker(int id, MarkerPoint[] corners)
    {
        var (side, ratio) = MarkerCandidateValidator.Measure(corners);
        return new DetectedMarker
        {
            Id = id,
            CornersPx = corners,
            CornersNormalized = corners,
            SidePx = side,
            EdgeRatio = ratio,
        };
    }
}
