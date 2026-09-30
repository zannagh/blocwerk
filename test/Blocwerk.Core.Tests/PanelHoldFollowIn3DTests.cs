// <copyright file="PanelHoldFollowIn3DTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.FollowIn3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hold added in the per-panel editor (<see cref="WallPanelService.AddPanelHoldAsync"/>) on a live panel gets the same
/// 3D follow-up as one added through <see cref="WallService.AddHoldAsync"/>; a hold added to a staged panel of an
/// in-flight update does not (it only goes live on promote). Placement geometry: <see cref="HoldEditFollowIn3DTests"/>.
/// </summary>
public class PanelHoldFollowIn3DTests
{
    [Fact]
    public async Task HoldAddedOnALivePanel_IsQueuedFor3D()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);

        var id = await Panels(h, s.Queue).AddPanelHoldAsync(h.WallId, s.PanelC0, 0.3, 0.45, 0.01);

        s.Queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(x => x.Single() == id));
        s.Queue.ReceivedWithAnyArgs(1).Enqueue(default, default!);
    }

    [Fact]
    public async Task HoldAddedOnAStagedPanel_IsNotQueued()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await using (var db = h.CreateContext())
        {
            var panel = await db.WallPanels.SingleAsync(p => p.Id == s.PanelC1);
            (panel.Generation, panel.StagedPhoto, panel.StagedPhotoContentType) = (2, [1, 2, 3], "image/jpeg");
            await db.SaveChangesAsync();
        }

        var id = await Panels(h, s.Queue).AddPanelHoldAsync(h.WallId, s.PanelC1, 0.3, 0.45, 0.01);

        await using (var db = h.CreateContext())
        {
            Assert.Equal(2, (await db.Holds.AsNoTracking().SingleAsync(x => x.Id == id)).Generation);
        }

        s.Queue.DidNotReceiveWithAnyArgs().Enqueue(default, default!);
    }

    [Fact]
    public async Task HoldAddedOnALivePanel_IsPlacedIn3D_AfterTheQueue()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var queue = Queue(h, s.Service());

        var id = await Panels(h, queue).AddPanelHoldAsync(h.WallId, s.PanelC0, 0.3, 0.45, 0.01);
        await queue.RunAsync(h.WallId, [id], default);

        var hold = (await s.LoadHoldsAsync())[id];
        Assert.Equal(("0", HoldMetric.TextureRegistration), (hold.FacetId, hold.MetricSource));
        Assert.Equal(1200, hold.PlaneAMm!.Value, 1);
        Assert.Equal(1650, hold.PlaneBMm!.Value, 1);
        Assert.Equal((s.PanelC0, 0.3, 0.45), (hold.WallPanelId!.Value, hold.X, hold.Y));
    }

    [Fact]
    public async Task ContainerRegistration_PassesTheQueueToTheService()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(h.DbContextFactory);
        services.AddSingleton<ICurrentUserService>(h.CurrentUser);
        services.AddSingleton(h.HoldDetection);
        services.AddSingleton(Substitute.For<IHoldOverlapMatcher>());
        services.AddSingleton(s.Queue);
        services.AddScoped<IWallPanelService, WallPanelService>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        var id = await scope.ServiceProvider.GetRequiredService<IWallPanelService>()
            .AddPanelHoldAsync(h.WallId, s.PanelC0, 0.3, 0.45, 0.01);

        s.Queue.Received(1).Enqueue(h.WallId, Arg.Is<IEnumerable<Guid>>(x => x.Single() == id));
    }

    private static WallPanelService Panels(WallTestHarness h, IHoldRefinementQueue queue) => new(
        h.DbContextFactory,
        h.CurrentUser,
        h.HoldDetection,
        Substitute.For<IHoldOverlapMatcher>(),
        NullLogger<WallPanelService>.Instance,
        refinementQueue: queue);
}
