// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>Style tags: who may set them (the editing rule), and that they survive a revision.</summary>
public sealed class BoulderStyleServiceTests
{
    private static async Task<BoulderStyle> StoredAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return (await db.Boulders.AsNoTracking().SingleAsync(b => b.Id == id)).Styles;
    }

    [Fact]
    public async Task TheCreatorCanTagTheirBoulder_WithSeveralStyles()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var creator = await h.AddMemberAsync("creator@test", WallRole.Member);
        h.ActingUser = creator;
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "Mine", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.SetBoulderStylesAsync(boulder.Id, BoulderStyle.Tension | BoulderStyle.Power);

        Assert.Equal(BoulderStyle.Tension | BoulderStyle.Power, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ASetterAndAWallAdminCanTagSomebodyElsesBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Theirs", null, [new BoulderHoldInput(holds[0].Id)], setterUserIds: [setter.Id]);

        h.ActingUser = setter;
        await h.BoulderService.SetBoulderStylesAsync(boulder.Id, BoulderStyle.Funky);
        h.ActingUser = admin;
        await h.BoulderService.SetBoulderStylesAsync(boulder.Id, BoulderStyle.Funky | BoulderStyle.Dynamic);

        Assert.Equal(BoulderStyle.Funky | BoulderStyle.Dynamic, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AnUnrelatedMemberCannotTag_AndTheTagsStayAsTheyWere()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var other = await h.AddMemberAsync("other@test", WallRole.Member);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Owner's", null, [new BoulderHoldInput(holds[0].Id)], styles: BoulderStyle.Technical);

        h.ActingUser = other;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.BoulderService.SetBoulderStylesAsync(boulder.Id, BoulderStyle.Power));

        Assert.Equal(BoulderService.StylesNotAllowedMessage, ex.Message);
        Assert.Equal(BoulderStyle.Technical, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task AStrayBitIsNeverStored()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)]);

        await h.BoulderService.SetBoulderStylesAsync(boulder.Id, (BoulderStyle)(2 | 4096));

        Assert.Equal(BoulderStyle.Dynamic, await StoredAsync(h, boulder.Id));
    }

    [Fact]
    public async Task ARevisionKeepsTheTags_WhenItCarriesNone_AndReplacesThemWhenItDoes()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "B", null, [new BoulderHoldInput(holds[0].Id)], styles: BoulderStyle.Endurance);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, [new BoulderHoldInput(holds[0].Id)], name: "B2");
        Assert.Equal(BoulderStyle.Endurance, await StoredAsync(h, boulder.Id));

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, [new BoulderHoldInput(holds[0].Id)], styles: BoulderStyle.Power);
        Assert.Equal(BoulderStyle.Power, await StoredAsync(h, boulder.Id));
    }
}
