// <copyright file="CaptureBlurFilterTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Blurry capture photos: scored on upload, and only the clearly blurry ones without a decoded marker are left out
/// of feature matching and training; the count shows on the capture.
/// </summary>
public class CaptureBlurFilterTests
{
    [Fact]
    public void OnlyAClearlyBlurryPhotoWithoutMarkers_IsLeftOut()
    {
        var photos = new List<WallCapturePhoto>
        {
            Photo(1, 900, true), Photo(2, 1000, false), Photo(3, 1100, false), Photo(4, 950, false),
            Photo(5, 150, false), Photo(6, 120, true), Photo(7, 400, false), Photo(8, null, false),
        };

        var blurry = CaptureBlurFilter.Blurry(photos, CaptureBlurFilter.DefaultRatio);

        // Reference: the 75th percentile of the scores (950); below 190 and no marker: photo 5 only.
        Assert.Equal([5], blurry.Order());
        Assert.Empty(CaptureBlurFilter.Blurry(photos, 0));
        Assert.Empty(CaptureBlurFilter.Blurry(photos.Skip(3).Take(4).ToList(), CaptureBlurFilter.DefaultRatio)); // 4 scored: not judged
    }

    [Fact]
    public void APhotoNotYetDetected_IsNeverJudged()
    {
        var photos = Enumerable.Range(1, 5).Select(i => Photo(i, 1000, false)).Append(Photo(6, 10, false)).ToList();
        photos[5].MarkersJson = null;

        Assert.Empty(CaptureBlurFilter.Blurry(photos, CaptureBlurFilter.DefaultRatio));
    }

    [Fact]
    public void BlurSettings_AreReadFromConfiguration()
    {
        var options = WallCapturePipelineOptions.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blocwerk:Capture:BlurExcludeRatio"] = "0.35",
                ["Blocwerk:Capture:PhotoSharpnessEdge"] = "2048",
            })
            .Build());

        Assert.Equal((0.35, 2048), (options.BlurExcludeRatio, options.PhotoSharpnessEdge));
        Assert.Equal((0.2, 1024), (new WallCapturePipelineOptions().BlurExcludeRatio, new WallCapturePipelineOptions().PhotoSharpnessEdge));
    }

    [Fact]
    public async Task TheBlurryPhotoWithoutMarkers_NeverReachesTheSplat_AndTheCaptureCountsIt()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        s.SplatClient.IsConfigured = true;
        s.Client.GeometryJson = CaptureScenario.GeometryWithCameras(Enumerable.Range(1, 5).Select(CaptureComputeDocuments.PhotoName).ToArray());
        var captureId = await s.StartCaptureAsync(photos: 6, beforeStart: async id =>
        {
            await using var db = h.CreateContext();
            await db.WallCapturePhotos.Where(p => p.CaptureId == id)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Sharpness, p => p.Index == 6 ? 10.0 : (double?)1000.0));
            await db.WallCapturePhotos.Where(p => p.CaptureId == id && p.Index == 6)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.MarkersJson, "[]"));
        });

        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var (_, splat) = Assert.Single(s.SplatClient.MultipartSubmissions);
        Assert.Equal(["p01.jpg", "p02.jpg", "p03.jpg", "p04.jpg", "p05.jpg"], splat.Where(p => p.Name == "photos").Select(p => p.FileName));
        var summary = (await s.Service.GetCaptureAsync(captureId))!;
        Assert.Equal((6, 1), (summary.PhotoCount, summary.BlurryPhotoCount));
    }

    private static WallCapturePhoto Photo(int index, double? sharpness, bool markers)
    {
        var marker = new CaptureMarker(markers ? 3 : 17, [[1, 1], [2, 1], [2, 2], [1, 2]], false, 50)
        {
            Ignored = markers ? null : CaptureMarkerDetection.NoQuietZoneReason,
        };
        return new WallCapturePhoto
        {
            Index = index,
            StoredPath = $"p{index}.jpg",
            ContentHash = $"h{index}",
            Sharpness = sharpness,
            MarkersJson = JsonSerializer.Serialize(new List<CaptureMarker> { marker }),
        };
    }
}
