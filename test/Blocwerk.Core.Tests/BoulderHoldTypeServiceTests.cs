// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Setting a boulder's dominant hold type: who may (the editing rule), clearing it, and that it survives a revision.</summary>
public sealed class BoulderHoldTypeServiceTests
{
    private static async Task<HoldHandType?> StoredAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return (await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == id)).DominantHoldType;
    }

    [Fact]
    public async Task TheCreatorCanSetIt_AndClearIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        h.ActingUser = await h.AddMemberAsync("creator@test", WallRole.Member);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "Mine", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, HoldHandType.Crimp);
        Assert.Equal(HoldHandType.Crimp, await StoredAsync(h, boulder.Id));

        await h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, null);
        Assert.Null(await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ASetterAndAWallAdminCanSetIt_OnSomebodyElsesBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Theirs", null, [new BoulderHoldInput(holds[0].Id)], setterUserIds: [setter.Id]);

        h.ActingUser = setter;
        await h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, HoldHandType.Pinch);
        Assert.Equal(HoldHandType.Pinch, await StoredAsync(h, boulder.Id));

        h.ActingUser = admin;
        await h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, HoldHandType.Jug);
        Assert.Equal(HoldHandType.Jug, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AnUnrelatedMemberIsRefused_AndTheTypeStays()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var other = await h.AddMemberAsync("other@test", WallRole.Member);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "Owner's", null, [new BoulderHoldInput(holds[0].Id)]);
        await h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, HoldHandType.Sloper);

        h.ActingUser = other;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, null));

        Assert.Equal(BoulderService.HoldTypeNotAllowedMessage, ex.Message);
        Assert.Equal(HoldHandType.Sloper, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AnUnknownTypeIsRefused()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, (HoldHandType)42));
        Assert.Null(await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionSetsAndClearsTheType_InTheSameSave_AndNullLeavesItAlone()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B2", holdType: new BoulderHoldTypeChange(HoldHandType.Pinch));
        Assert.Equal(HoldHandType.Pinch, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B3");
        Assert.Equal(HoldHandType.Pinch, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B3", holdType: new BoulderHoldTypeChange(null));
        Assert.Null(await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionChangingOnlyTheType_IsNoReplay_AndAnUnknownTypeIsRefused()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: new BoulderHoldTypeChange(HoldHandType.Jug));
        Assert.Equal(HoldHandType.Jug, await StoredAsync(h, boulder.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: new BoulderHoldTypeChange((HoldHandType)42)));
        Assert.Equal(HoldHandType.Jug, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionKeepsTheType()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
        await h.BoulderService.SetBoulderHoldTypeAsync(boulder.Id, HoldHandType.Pocket);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, [new BoulderHoldInput(holds[0].Id)], name: "B2");

        Assert.Equal(HoldHandType.Pocket, await StoredAsync(h, boulder.Id));
    }
}
