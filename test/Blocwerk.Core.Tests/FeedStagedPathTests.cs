// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.StructureFeedKit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The last staged write paths (stage and discard a panel, a hold on a staged panel) stay with the setters, and an
/// audience narrowing does not outlive its operation in work it started.
/// </summary>
public sealed class FeedStagedPathTests
{
    [Fact]
    public async Task ANarrowingInheritedByFireAndForgetWork_EndsWithItsOperation()
    {
        using var harness = new WallTestHarness();
        var hold = (await harness.SeedWallAsync(holdCount: 1))[0];
        var gate = new TaskCompletionSource();
        Task background;
        using (FeedAudienceScope.Begin(FeedAudience.Setters))
        {
            background = Task.Run(async () =>
            {
                await gate.Task;
                await using var db = Context(harness);
                (await db.Holds.SingleAsync()).X = 0.7;
                await db.SaveChangesAsync();
            });
        }

        gate.SetResult();
        await background;

        var row = Assert.Single(await RowsAsync(harness));
        Assert.Equal((FeedKinds.HoldChanged, hold.Id, FeedAudience.Wall), (row.Kind, row.EntityId!.Value, row.Audience));
    }

    [Fact]
    public async Task StagingAndDiscardingAPanel_WriteOneResyncEach_ForTheSetters()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        await SeedPanelAsync(harness, new WallPanel { WallId = harness.WallId, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" });
        WallUpdateSessionFixture.NoDetections(harness);
        var panels = Panels(harness);

        var staged = await panels.StagePanelAsync(harness.WallId, 0, 1, [9], "image/jpeg");
        await panels.DiscardPanelAsync(harness.WallId, staged.PanelId);

        var rows = await RowsAsync(harness);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((FeedKinds.WallResync, FeedAudience.Setters), (r.Kind, r.Audience)));
        Assert.Contains("panel-stage", rows[0].DataJson);
        Assert.Contains("panel-discard", rows[1].DataJson);
    }

    [Fact]
    public async Task AHoldOnAStagedPanel_GoesToTheSetters_AndOneOnALivePanelToTheWall()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var live = await SeedPanelAsync(harness, new WallPanel { WallId = harness.WallId, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" });
        var staged = await SeedPanelAsync(
            harness, new WallPanel { WallId = harness.WallId, Col = 1, StagedPhoto = [4], StagedPhotoContentType = "image/jpeg", Generation = 1 });
        var panels = Panels(harness);

        var onStaged = await panels.AddPanelHoldAsync(harness.WallId, staged, 0.5, 0.5, 0.02);
        var onLive = await panels.AddPanelHoldAsync(harness.WallId, live, 0.5, 0.5, 0.02);

        var rows = (await RowsAsync(harness)).Where(r => r.Kind == FeedKinds.HoldChanged).ToDictionary(r => r.EntityId!.Value);
        Assert.Equal(FeedAudience.Setters, rows[onStaged].Audience);
        Assert.Equal(FeedAudience.Wall, rows[onLive].Audience);
    }

    [Theory]
    [InlineData(25, "hold-refinement")]
    [InlineData(3, null)]
    public async Task TheRefinementQueue_RunsALargeSetInsideAScope_AndAFewEditedHoldsOutsideOne(int holds, string? label)
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        string? seen = "unset";
        var footprints = Substitute.For<IHoldFootprintService>();
        footprints.RefineHoldsFromPipelineAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                seen = FeedScope.Active?.Label;
                return Task.FromResult<HoldFootprintRunResult?>(null);
            });
        var services = new ServiceCollection()
            .AddSingleton<IDbContextFactory<Data.BlocwerkDbContext>>(harness.DbContextFactory)
            .AddSingleton(footprints)
            .BuildServiceProvider();
        var queue = new HoldRefinementQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<HoldRefinementQueue>.Instance);

        await queue.RunAsync(harness.WallId, Enumerable.Range(0, holds).Select(_ => Guid.NewGuid()).ToList(), CancellationToken.None);

        Assert.Equal(label, seen);
        Assert.Null(FeedScope.Active);
    }

    private static WallPanelService Panels(WallTestHarness harness) => new(
        new InterceptedDbContextFactory(harness.DbContextFactory.ConnectionString, [Interceptor()]),
        harness.CurrentUser, harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallPanelService>.Instance);

    private static async Task<Guid> SeedPanelAsync(WallTestHarness harness, WallPanel panel)
    {
        await using var db = harness.CreateContext();
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }
}
