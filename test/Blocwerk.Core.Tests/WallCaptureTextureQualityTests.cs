// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The quality choice of "Render wall textures again": which worker options each quality sends, that the choice survives
/// on the capture's mark until the worker runs it, that it is rendered with those options, and what the estimate says
/// about the worker's blend budget (The Attic's real numbers).
/// </summary>
public class WallCaptureTextureQualityTests
{
    [Fact]
    public void Standard_SendsTheConfiguredSettings_AndNothingWhenNoneAreSet()
    {
        Assert.Null(TextureQualityPresets.ToOptionsJson(TextureQuality.Standard, new GeometryTextureSettings()));
        var json = TextureQualityPresets.ToOptionsJson(TextureQuality.Standard, new GeometryTextureSettings { MmPerPx = 3, JpegQuality = 80 });
        Assert.Equal(3, (double)JsonNode.Parse(json!)!["mmPerPx"]!);
        Assert.Null(JsonNode.Parse(json!)!["blendViews"]);
    }

    [Theory]
    [InlineData(TextureQuality.High, 1.5, 6)]
    [InlineData(TextureQuality.Maximum, 1.25, 4)]
    public void HigherQualities_AreFinerAndKeepTheBlendWithinTheWorkerBudget(TextureQuality quality, double mm, int views)
    {
        var options = JsonNode.Parse(TextureQualityPresets.ToOptionsJson(quality, new GeometryTextureSettings { JpegQuality = 85 })!)!;
        Assert.Equal((mm, 8192, views, 85), ((double)options["mmPerPx"]!, (int)options["maxSidePx"]!, (int)options["blendViews"]!, (int)options["jpegQuality"]!));
    }

    [Fact]
    public void AQuality_NeverMakesTheConfiguredTextureCoarser()
    {
        var finer = new GeometryTextureSettings { MmPerPx = 1.0, MaxSidePx = 8192 };
        Assert.Equal(1.0, TextureQualityPresets.Resolve(TextureQuality.High, finer).MmPerPx);
        Assert.Equal(1.0, TextureQualityPresets.Resolve(TextureQuality.Maximum, finer).MmPerPx);
        Assert.Equal(2.0, TextureQualityPresets.Resolve(TextureQuality.Standard, new GeometryTextureSettings()).MmPerPx);
    }

    [Theory]
    [InlineData(TextureQuality.Standard, null, "rerender:")]
    [InlineData(TextureQuality.High, null, "rerender:q1:")]
    [InlineData(TextureQuality.Maximum, "abc123", "rerender:q2:abc123")]
    [InlineData(TextureQuality.Standard, "abc123", "rerender:abc123")]
    public void TheMark_CarriesTheQuality_AndTheSubmittedJob(TextureQuality quality, string? jobId, string expected)
    {
        var mark = CaptureTextureOutcome.Mark(quality, jobId);
        Assert.Equal(expected, mark);
        Assert.True(CaptureTextureOutcome.IsRerendering(mark));
        Assert.Equal(quality, CaptureTextureOutcome.RerenderQuality(mark));
        Assert.Equal(jobId, CaptureTextureOutcome.RerenderJobId(mark));
    }

    [Fact]
    public void AMarkWrittenBeforeQualitiesExisted_IsStandard()
    {
        Assert.Equal(TextureQuality.Standard, CaptureTextureOutcome.RerenderQuality("rerender:job-7"));
        Assert.Equal("job-7", CaptureTextureOutcome.RerenderJobId("rerender:job-7"));
        Assert.Equal(TextureQuality.Standard, CaptureTextureOutcome.RerenderQuality(null));
    }

    [Theory]
    [InlineData(TextureQuality.Standard)]
    [InlineData(TextureQuality.High)]
    [InlineData(TextureQuality.Maximum)]
    public async Task Rerender_SendsTheOptionsOfTheChosenQuality_ToTheTexturesJob(TextureQuality quality)
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Queue.DequeueAsync(CancellationToken.None);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Empty(await s.Service.RerenderTexturesAsync(captureId, quality));
        await s.Processor.RerenderTexturesAsync(captureId, CancellationToken.None);

        var parts = s.Client.MultipartSubmissions[^1];
        Assert.Equal("textures", parts.Kind);
        var options = parts.Parts.FirstOrDefault(p => p.Name == "options");
        if (quality == TextureQuality.Standard)
        {
            Assert.Null(options);
            return;
        }

        var json = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(options!.Content))!;
        Assert.Equal(TextureQualityPresets.Resolve(quality, new GeometryTextureSettings()).MmPerPx, (double)json["mmPerPx"]!);
    }

    [Fact]
    public async Task AnUnknownQuality_IsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var captureId = await s.StartCaptureAsync();
        await s.Queue.DequeueAsync(CancellationToken.None);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        Assert.Equal(["Unknown texture quality."], await s.Service.RerenderTexturesAsync(captureId, (TextureQuality)9));
    }

    [Fact]
    public void TheAtticEstimates_MatchTheWorkersMeasuredBlendMemory()
    {
        // 8 facets as in test/real-data/attic/model.json and 356 photos: measured with textures.blend_bytes in the worker
        // at 2.0 / 1.5 / 1.25 mm per pixel: 10.5 / 18.7 / 26.9 MP and 1.15 / 1.58 / 2.07 GB (six views)
        var json = File.ReadAllText(Path.Combine(AtticDir(), "model.json"));
        var all = TextureQualityEstimate.ForAll(json, 356, new GeometryTextureSettings());

        var standard = all.Single(e => e.Quality == TextureQuality.Standard);
        var high = all.Single(e => e.Quality == TextureQuality.High);
        var max = all.Single(e => e.Quality == TextureQuality.Maximum);
        Assert.InRange(standard.MegaPixels, 10.0, 11.0);
        Assert.InRange(standard.BlendGb, 1.0, 1.3);
        Assert.Equal(TextureBlendFit.Full, standard.Fit);
        Assert.InRange(high.MegaPixels, 18.0, 19.5);
        Assert.InRange(high.BlendGb, 1.4, 1.8);
        Assert.Equal(TextureBlendFit.Full, high.Fit);
        Assert.Equal(4, max.BlendViews);
        Assert.Equal(TextureBlendFit.Full, max.Fit);
        Assert.All(all, e => Assert.True(e.BlendGb * 1e9 <= TextureQualityEstimate.BlendBudgetBytes));
    }

    [Fact]
    public void ABiggerWall_IsWarnedAboutInsteadOfSilentlyFallingBackToOnePhoto()
    {
        var wall = new List<(double A, double B)> { (20000, 12000) };
        var fits = TextureQualityEstimate.For(TextureQuality.Standard, wall, 300, new GeometryTextureSettings());
        var fine = TextureQualityEstimate.For(TextureQuality.Maximum, [(60000, 40000), (60000, 40000)], 300, new GeometryTextureSettings());
        Assert.NotEqual(TextureBlendFit.SingleView, fits.Fit);
        Assert.Contains(fine.Fit, new[] { TextureBlendFit.SingleView, TextureBlendFit.TooLarge });
    }

    private static string AtticDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test", "real-data", "attic");
            if (File.Exists(Path.Combine(candidate, "model.json")))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("test/real-data/attic/model.json");
    }
}
