// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Runners;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Where "Render textures again" runs: on the host while the quality fits, else on an online runner, else the best that fits.</summary>
public class TextureRoutingTests
{
    private static readonly TextureRunnerOffer Mac = new("Mac", false, false, 0, 0);

    [Fact]
    public void AQualityThatFitsTheHost_RunsThere_EvenWhenARunnerIsOnline()
    {
        var routing = TextureRoutes.Decide(Estimates(TextureBlendFit.Full, TextureBlendFit.Full, TextureBlendFit.Full), _ => Mac, true);

        Assert.All(routing.Routes, r => Assert.Equal((TextureRoute.Host, (string?)null), (r.Route, r.Note)));
        Assert.Equal(TextureQuality.Maximum, routing.BestHostQuality);
    }

    [Theory]
    [InlineData(TextureBlendFit.FewerViews)]
    [InlineData(TextureBlendFit.SingleView)]
    [InlineData(TextureBlendFit.TooLarge)]
    public void AQualityThatDoesNotFit_GoesToTheRunner_WhenOneCanRenderItInFull(TextureBlendFit fit)
    {
        var routing = TextureRoutes.Decide(Estimates(TextureBlendFit.Full, TextureBlendFit.Full, fit), q => q == TextureQuality.Maximum ? Mac : null, true);

        var max = routing.RouteOf(TextureQuality.Maximum);
        Assert.Equal((TextureRoute.Runner, "Mac"), (max.Route, max.Runner?.Name));
        Assert.Equal(TextureRoute.Host, routing.RouteOf(TextureQuality.High).Route);
        Assert.Equal(TextureQuality.High, routing.BestHostQuality);
    }

    [Fact]
    public void WithNoCapableRunnerOnline_SaysSo_AndOffersTheBestQualityThatFits()
    {
        var routing = TextureRoutes.Decide(Estimates(TextureBlendFit.Full, TextureBlendFit.SingleView, TextureBlendFit.SingleView), _ => null, false);

        var high = routing.RouteOf(TextureQuality.High);
        Assert.Equal((TextureRoute.Host, "No 3D runner that renders textures is online."), (high.Route, high.Note));
        Assert.Equal(TextureQuality.Standard, routing.BestHostQuality);
    }

    [Fact]
    public void ARunnerOnlineWithTooLittleMemory_IsToldApart_FromNoRunner()
    {
        var routing = TextureRoutes.Decide(Estimates(TextureBlendFit.Full, TextureBlendFit.Full, TextureBlendFit.FewerViews), _ => null, true);

        Assert.Equal("No online 3D runner has enough memory for this either.", routing.RouteOf(TextureQuality.Maximum).Note);
    }

    [Fact]
    public void ThePausedAndBusyState_ComesWithTheOffer()
    {
        var routing = TextureRoutes.Decide(
            Estimates(TextureBlendFit.Full, TextureBlendFit.Full, TextureBlendFit.FewerViews), _ => new TextureRunnerOffer("Mac", true, true, 2, 1), true);

        Assert.Equal(new TextureRunnerOffer("Mac", true, true, 2, 1), routing.RouteOf(TextureQuality.Maximum).Runner);
    }

    [Fact]
    public void TheRunnersBudget_LetsAQualityBlendInFull_ThatTheHostCannot()
    {
        List<(double A, double B)> wall = [(40000, 12000), (30000, 12000)];
        var configured = new GeometryTextureSettings();

        var host = TextureQualityEstimate.For(TextureQuality.Maximum, wall, 400, configured);
        var runner = TextureQualityEstimate.For(TextureQuality.Maximum, wall, 400, configured, budgetBytes: 24e9, maxPixels: 2e9);

        Assert.NotEqual(TextureBlendFit.Full, host.Fit);
        Assert.Equal(TextureBlendFit.Full, runner.Fit);
        var needed = TextureQualityEstimate.FullBlendBytes(TextureQuality.Maximum, wall, 400, configured);
        Assert.InRange(needed, TextureQualityEstimate.BlendBudgetBytes, 24e9);
    }

    [Fact]
    public async Task TheService_RoutesByTheOnlineRunnersMemory_AndPausedOnesAreStillOffered()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { ClaimWait = TimeSpan.Zero });
        s.Settings.GeometryTextures.MmPerPx = 0.4; // fine enough that the highest qualities do not fit the host
        var captureId = await s.StartCaptureAsync();
        await s.Queue.DequeueAsync(CancellationToken.None);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var none = await s.Service.GetTextureRoutingAsync(captureId);
        var runner = await TexturesJobSupport.AddTexturesRunnerAsync(h, s.Runners!, "Mac", memoryMb: 48000);
        var online = await s.Service.GetTextureRoutingAsync(captureId);
        await using (var db = h.CreateContext())
        {
            await db.GpuRunners.Where(r => r.Id == runner.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Paused, true));
        }

        var paused = await s.Service.GetTextureRoutingAsync(captureId);
        await using (var db = h.CreateContext())
        {
            await db.GpuRunners.Where(r => r.Id == runner.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.TexturesMemoryMb, 100));
        }

        var small = await s.Service.GetTextureRoutingAsync(captureId);

        var worst = none.Estimates.Where(e => e.Fit != TextureBlendFit.Full).Select(e => e.Quality).DefaultIfEmpty().Max();
        Assert.NotEqual(TextureBlendFit.Full, none.Estimates.Single(e => e.Quality == worst).Fit);
        Assert.False(none.RunnerOnline);
        Assert.Equal(TextureRoute.Host, none.RouteOf(worst).Route);
        Assert.Contains("is online", none.RouteOf(worst).Note);
        Assert.Equal((TextureRoute.Runner, "Mac", false), (online.RouteOf(worst).Route, online.RouteOf(worst).Runner?.Name, online.RouteOf(worst).Runner?.Paused));
        Assert.True(paused.RouteOf(worst).Runner?.Paused);
        Assert.Equal(TextureRoute.Host, small.RouteOf(worst).Route);
        Assert.True(small.RunnerOnline);
    }

    [Fact]
    public async Task WithRunnersOff_NothingIsOffered()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h, runnerOptions: new GpuRunnerOptions { Mode = GpuRunnerMode.Off });
        s.Settings.GeometryTextures.MmPerPx = 0.4;
        var captureId = await s.StartCaptureAsync();
        await s.Queue.DequeueAsync(CancellationToken.None);
        await s.Processor.ProcessAsync(captureId, CancellationToken.None);

        var routing = await s.Service.GetTextureRoutingAsync(captureId);

        Assert.All(routing.Routes, r => Assert.Equal(TextureRoute.Host, r.Route));
        Assert.Contains("switched off", Assert.Single(await s.Service.RerenderTexturesAsync(captureId, TextureQuality.High, TextureRoute.Runner)));
    }

    private static List<TextureQualityEstimate> Estimates(TextureBlendFit standard, TextureBlendFit high, TextureBlendFit max) =>
    [
        new(TextureQuality.Standard, 2, 10, 1, 6, standard, 1),
        new(TextureQuality.High, 1.5, 18, 1.5, 6, high, 2),
        new(TextureQuality.Maximum, 1.25, 27, 2, 4, max, 3),
    ];
}
