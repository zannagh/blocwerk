// <copyright file="HoldProposalAcceptTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hold proposal's panel point is in the coordinates of the panel photo that was live when the search ran. Accepting it
/// creates the hold there while that photo is live; once a panel update replaced the photo (or while an update is staged)
/// it is refused with a clear message and stays pending, rather than creating a hold the wall never shows.
/// </summary>
public class HoldProposalAcceptTests
{
    [Fact]
    public async Task Accept_OnTheLivePanel_CreatesTheHoldThere()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panel = await AddPanelAsync(h, generation: 0);
        var proposal = await AddProposalAsync(h, panel);

        var hold = await Service(h).AcceptAsync(h.WallId, proposal, null, HoldCategory.Hand);

        Assert.Equal(panel, hold.WallPanelId);
        Assert.Equal(HoldProposalStatus.Accepted, await StatusAsync(h, proposal));
    }

    [Fact]
    public async Task Accept_AfterAPanelUpdateReplacedThePanel_IsRefused_AndStaysPending()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var old = await AddPanelAsync(h, generation: 0);
        var proposal = await AddProposalAsync(h, old);
        await AddPanelAsync(h, generation: 1);

        var refusal = await Assert.ThrowsAsync<UserFacingException>(() => Service(h).AcceptAsync(h.WallId, proposal, null, HoldCategory.Hand));

        Assert.Contains("replaced", refusal.Message);
        Assert.Equal(HoldProposalStatus.Pending, await StatusAsync(h, proposal));
        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.WallPanelId == old));
    }

    [Fact]
    public async Task List_FlagsAProposalOnAReplacedPanel_AsStale()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var old = await AddPanelAsync(h, generation: 0);
        var proposal = await AddProposalAsync(h, old);
        Assert.False(Assert.Single(await Service(h).ListAsync(h.WallId)).IsStale);

        await AddPanelAsync(h, generation: 1);

        var listed = Assert.Single(await Service(h).ListAsync(h.WallId));
        Assert.Equal(proposal, listed.Id);
        Assert.True(listed.IsStale);
    }

    [Fact]
    public async Task Accept_WhileAPanelUpdateIsStaged_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var panel = await AddPanelAsync(h, generation: 0);
        var proposal = await AddProposalAsync(h, panel);
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            wall.StagedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var refusal = await Assert.ThrowsAsync<UserFacingException>(() => Service(h).AcceptAsync(h.WallId, proposal, null, HoldCategory.Hand));

        Assert.Contains("panel update", refusal.Message);
        Assert.Equal(HoldProposalStatus.Pending, await StatusAsync(h, proposal));
    }

    [Fact]
    public async Task AddHold_OnASupersededPanel_IsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var old = await AddPanelAsync(h, generation: 0);
        await AddPanelAsync(h, generation: 1);

        await Assert.ThrowsAsync<UserFacingException>(() => h.WallService.AddHoldAsync(h.WallId, 0.5, 0.5, 0.02, null, wallPanelId: old));
    }

    private static HoldProposalService Service(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, h.WallService, Array.Empty<ICaptureHoldDetector>(), NullLogger<HoldProposalService>.Instance);

    private static async Task<Guid> AddPanelAsync(WallTestHarness h, int generation)
    {
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        wall.CurrentGeneration = generation;
        var panel = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Generation = generation, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static async Task<Guid> AddProposalAsync(WallTestHarness h, Guid panelId)
    {
        await using var db = h.CreateContext();
        var model = new WallGeometryModel { WallId = h.WallId, Json = "{}", Source = "test", IsActive = true };
        db.WallGeometryModels.Add(model);
        var proposal = new HoldProposal
        {
            WallId = h.WallId, GeometryModelId = model.Id, FacetId = "0", BestPhoto = "p.jpg", Views = 3, Confidence = 0.9,
            PanelId = panelId, PanelX = 0.4, PanelY = 0.6, PanelRadius = 0.02,
        };
        db.HoldProposals.Add(proposal);
        await db.SaveChangesAsync();
        return proposal.Id;
    }

    private static async Task<HoldProposalStatus> StatusAsync(WallTestHarness h, Guid proposalId)
    {
        await using var db = h.CreateContext();
        return await db.HoldProposals.Where(p => p.Id == proposalId).Select(p => p.Status).SingleAsync();
    }
}
