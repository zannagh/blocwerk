// <copyright file="WallRefreshActorFactoryTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Refresh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The worker builds the services it drives from the app's container, acting as the admin who started the run:
/// the constructors must resolve with the registrations the app has, and the actors act as that admin.
/// </summary>
public class WallRefreshActorFactoryTests
{
    [Fact]
    public async Task Actors_ResolveFromTheContainer_AndActAsTheStartingAdmin()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new BlocwerkSettings());
        services.AddSingleton<RootDbContextFactory>(h.RootContextFactory);
        services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(h.DbContextFactory);
        services.AddSingleton(h.HoldDetection);
        services.AddSingleton(Substitute.For<IHoldOverlapMatcher>());
        services.AddWallCapture();
        services.AddWallRefresh();
        await using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IWallRefreshActorFactory>();
        await using var scope = await factory.CreateAsync(h.Owner.Id, CancellationToken.None);

        Assert.IsType<WallCaptureService>(scope.Actors.Capture);
        Assert.NotNull(scope.Actors.Placement);
        var draft = await scope.Actors.Capture.GetDraftAsync(h.WallId);
        Assert.Null(draft);
        Assert.Null(await scope.Actors.Sessions.GetOpenSessionAsync(h.WallId));
    }
}
