// <copyright file="HoldColorWorkerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Data;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// The background worker applies an update with services it builds from the app's container
/// (<see cref="IWallRefreshActorFactory"/>): the promote it runs proposes colours like the page's, with or without a
/// proposer registered.
/// </summary>
public class HoldColorWorkerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePromoteTheWorkerRuns_ProposesColoursForItsNewHolds(bool proposerRegistered)
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0, generation: 1, staged: true);
        var fresh = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot, generation: 1);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton(new BlocwerkSettings());
        services.AddSingleton<RootDbContextFactory>(s.Harness.RootContextFactory);
        services.AddSingleton<IDbContextFactory<BlocwerkDbContext>>(s.Harness.DbContextFactory);
        services.AddSingleton(s.Harness.HoldDetection);
        services.AddSingleton(Substitute.For<IHoldOverlapMatcher>());
        services.AddWallCapture();
        services.AddWallRefresh();
        if (proposerRegistered)
        {
            services.AddSingleton<IHoldColorProposer, HoldColorProposer>();
        }

        await using var provider = services.BuildServiceProvider();
        await using var scope = await provider.GetRequiredService<IWallRefreshActorFactory>().CreateAsync(s.Harness.Owner.Id, CancellationToken.None);

        await scope.Actors.BigUpdate.PromoteAsync(s.WallId, new BigUpdateConfirmation([], [fresh], [], []));

        var hold = await s.GetAsync(fresh);
        Assert.Equal((1, "blue", true), (hold.Generation, hold.Color, hold.HasProposedColor));
    }
}
