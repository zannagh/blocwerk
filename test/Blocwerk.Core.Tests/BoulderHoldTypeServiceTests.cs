// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Setting a boulder's hold types: who may (the editing rule), several at once, clearing them, and that they survive a revision.</summary>
public sealed class BoulderHoldTypeServiceTests
{
    private static async Task<BoulderHoldType> StoredAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return (await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == id)).HoldTypes;
    }

    [Fact]
    public async Task TheCreatorCanSetSeveral_AndClearThem()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        h.ActingUser = await h.AddMemberAsync("creator@test", WallRole.Member);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "Mine", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.Crimp | BoulderHoldType.HeelHook | BoulderHoldType.ToeHook);
        Assert.Equal(BoulderHoldType.Crimp | BoulderHoldType.HeelHook | BoulderHoldType.ToeHook, await StoredAsync(h, boulder.Id));

        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.None);
        Assert.Equal(BoulderHoldType.None, await StoredAsync(h, boulder.Id));
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
        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.Pinch);
        Assert.Equal(BoulderHoldType.Pinch, await StoredAsync(h, boulder.Id));

        h.ActingUser = admin;
        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.Jug | BoulderHoldType.HeelHook);
        Assert.Equal(BoulderHoldType.Jug | BoulderHoldType.HeelHook, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AnUnrelatedMemberIsRefused_AndTheTypeStays()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var other = await h.AddMemberAsync("other@test", WallRole.Member);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "Owner's", null, [new BoulderHoldInput(holds[0].Id)]);
        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.Sloper | BoulderHoldType.ToeHook);

        h.ActingUser = other;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "Theirs now", holdType: new BoulderHoldTypeChange(BoulderHoldType.Jug)));

        Assert.Equal(BoulderService.HoldTypeNotAllowedMessage, ex.Message);
        Assert.Equal(BoulderHoldType.Sloper | BoulderHoldType.ToeHook, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AnUnknownTypeIsRefused()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, (BoulderHoldType)128));
        Assert.Equal(BoulderHoldType.None, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionSetsAndClearsTheTypes_InTheSameSave_AndNullLeavesThemAlone()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        var pinchAndHeel = BoulderHoldType.Pinch | BoulderHoldType.HeelHook;
        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B2", holdType: new BoulderHoldTypeChange(pinchAndHeel));
        Assert.Equal(pinchAndHeel, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B3");
        Assert.Equal(pinchAndHeel, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B3", holdType: new BoulderHoldTypeChange(BoulderHoldType.None));
        Assert.Equal(BoulderHoldType.None, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionChangingOnlyTheTypes_IsNoReplay_AndAnUnknownTypeIsRefused()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: new BoulderHoldTypeChange(BoulderHoldType.Jug));
        Assert.Equal(BoulderHoldType.Jug, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: new BoulderHoldTypeChange(BoulderHoldType.Jug | BoulderHoldType.ToeHook));
        Assert.Equal(BoulderHoldType.Jug | BoulderHoldType.ToeHook, await StoredAsync(h, boulder.Id));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: new BoulderHoldTypeChange((BoulderHoldType)512)));
        Assert.Equal(BoulderHoldType.Jug | BoulderHoldType.ToeHook, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionKeepsTheTypes()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.Pocket | BoulderHoldType.HeelHook);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, [new BoulderHoldInput(holds[0].Id)], name: "B2");

        Assert.Equal(BoulderHoldType.Pocket | BoulderHoldType.HeelHook, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AnOldClientsSingleType_ReplacesTheGrips_AndKeepsTheHooks()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);
        await h.BoulderService.SetBoulderHoldTypesAsync(boulder.Id, BoulderHoldType.Crimp | BoulderHoldType.Jug | BoulderHoldType.HeelHook);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: BoulderHoldTypeChange.FromRequest(null, "Sloper"));
        Assert.Equal(BoulderHoldType.Sloper | BoulderHoldType.HeelHook, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B", holdType: BoulderHoldTypeChange.FromRequest(null, string.Empty));
        Assert.Equal(BoulderHoldType.HeelHook, await StoredAsync(h, boulder.Id));
    }
}
