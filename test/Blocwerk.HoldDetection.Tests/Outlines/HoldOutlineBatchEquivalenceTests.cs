// <copyright file="HoldOutlineBatchEquivalenceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// The batch form gives exactly what the stateful session gives for the same photo bytes and seeds: per seed for
/// <see cref="HoldOutlineBatchOptions.Separately"/>, jointly for <see cref="HoldOutlineBatchOptions.Jointly"/>.
/// </summary>
public class HoldOutlineBatchEquivalenceTests
{
    [Fact]
    public async Task Separately_EqualsOutliningEachSeedOnASession()
    {
        var (photo, seeds) = Wall();
        var service = new OpenCvHoldOutlineService();

        var batch = await new SessionHoldOutlineBatch(service, null).OutlineAsync(photo, seeds, HoldOutlineBatchOptions.Separately);

        using var session = service.OpenSession(photo);
        Assert.Equal((session.ImageWidth, session.ImageHeight), (batch.ImageWidth, batch.ImageHeight));
        Assert.Equal(seeds.Select(s => Json(session.Outline(s))), batch.Outlines.Select(Json));
    }

    [Fact]
    public async Task Jointly_EqualsTheSessionsJointOutlining()
    {
        var (photo, seeds) = Wall();
        var service = new OpenCvHoldOutlineService();

        var batch = await new SessionHoldOutlineBatch(service, null).OutlineAsync(photo, seeds, HoldOutlineBatchOptions.Jointly);

        using var session = service.OpenSession(photo);
        Assert.Equal(session.OutlineEach(seeds).Select(Json), batch.Outlines.Select(Json));
    }

    // Three distinct painted holds (boxed seeds), a circle seed on bare wood and a seed off the photo, encoded the way
    // photos arrive. All stay on the colour or circle path: GrabCut draws from OpenCV's per-thread RNG.
    private static (byte[] Photo, List<HoldSeed> Seeds) Wall()
    {
        using Mat img = SyntheticWall.Wood(800, 600);
        var seeds = new List<HoldSeed>();
        (Point Centre, Scalar Colour)[] holds = [(new(200, 180), new(40, 40, 200)), (new(520, 210), new(30, 160, 60)), (new(400, 430), new(200, 90, 30))];
        foreach (var (centre, colour) in holds)
        {
            Point[] outline = SyntheticWall.Blob(centre, 60, 45, seed: centre.X);
            SyntheticWall.PaintHold(img, outline, colour, 0);
            Rect box = Cv2.BoundingRect(outline);
            seeds.Add(HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height));
        }

        seeds.Add(new HoldSeed(0.85, 0.85, 0.03));
        seeds.Add(new HoldSeed(1.4, -0.2, 0.02));
        return (img.ImEncode(".png"), seeds);
    }

    private static string Json(HoldOutlineResult result) => JsonSerializer.Serialize(result);
}
