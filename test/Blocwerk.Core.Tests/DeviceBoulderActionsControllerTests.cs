// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The web boulder page's actions for the companion app (publish, archive, grade proposals, variant links), under the
/// boulder service's one authority rule: the creator, a setter or a wall admin acts, anyone but the creator proposes, an
/// outsider gets 404. Who may call at all is <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceBoulderActionsControllerTests
{
    [Fact]
    public async Task TheCreator_PublishesADraft_OthersCannot_AndOneWithoutHoldsIs409()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var draft = await h.BoulderService.CreateBoulderAsync(h.WallId, "Draft", "6A", [new BoulderHoldInput(holds[0].Id)], isDraft: true);
        var empty = await h.BoulderService.CreateBoulderAsync(h.WallId, "Empty", "6A", [], isDraft: true);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);

        h.ActingUser = member;
        Refused(await Controller(h).Publish(h.WallId, draft.Id, default), 403);
        h.ActingUser = h.Owner;
        Assert.IsType<OkObjectResult>(await Controller(h).Publish(h.WallId, draft.Id, default));
        Assert.IsType<OkObjectResult>(await Controller(h).Publish(h.WallId, draft.Id, default)); // a retry is a no-op
        Refused(await Controller(h).Publish(h.WallId, empty.Id, default), 409);

        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == draft.Id)).IsDraft);
    }

    [Fact]
    public async Task AHistoricBoulder_IsArchivedAndRestored_ALiveOneIs409()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var live = await h.BoulderService.CreateBoulderAsync(h.WallId, "Live", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var historic = await h.BoulderService.CreateBoulderAsync(h.WallId, "Old", "6A", [new BoulderHoldInput(holds[1].Id)]);
        await using (var db = h.CreateContext())
        {
            await db.Boulders.Where(b => b.Id == historic.Id).ExecuteUpdateAsync(u => u.SetProperty(b => b.IsHistoric, true));
        }

        Refused(await Controller(h).Archive(h.WallId, live.Id, default), 409);
        Assert.IsType<OkObjectResult>(await Controller(h).Archive(h.WallId, historic.Id, default));
        Assert.True(await ArchivedAsync(h, historic.Id));
        Assert.IsType<OkObjectResult>(await Controller(h).Unarchive(h.WallId, historic.Id, default));
        Assert.False(await ArchivedAsync(h, historic.Id));
    }

    [Fact]
    public async Task AMemberProposesAGrade_TheCreatorAcceptsIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);

        h.ActingUser = member;
        Refused(await Controller(h).Propose(h.WallId, boulder.Id, new DeviceBoulderActionRequests { Grade = "6Z" }, default), 400);
        var proposed = State(await Controller(h).Propose(h.WallId, boulder.Id, new DeviceBoulderActionRequests { Grade = "7A" }, default));
        Assert.Equal(("7A", member.Id, true, false), (proposed.Proposal!.ProposedGrade, proposed.Proposal.ProposedByUserId, proposed.CanPropose, proposed.CanResolve));
        Refused(await Controller(h).Accept(h.WallId, boulder.Id, proposed.Proposal.Id, Expect("7A"), default), 403);

        h.ActingUser = h.Owner;
        var asCreator = State(await Controller(h).GetProposal(h.WallId, boulder.Id, default));
        Assert.Equal((false, true), (asCreator.CanPropose, asCreator.CanResolve));
        Refused(await Controller(h).Propose(h.WallId, boulder.Id, new DeviceBoulderActionRequests { Grade = "6B" }, default), 403);
        Refused(await Controller(h).Accept(h.WallId, boulder.Id, Guid.NewGuid(), Expect("7A"), default), 404);
        Refused(await Controller(h).Accept(h.WallId, boulder.Id, proposed.Proposal.Id, new DeviceBoulderActionRequests(), default), 400);
        Assert.Null(State(await Controller(h).Accept(h.WallId, boulder.Id, proposed.Proposal.Id, Expect("7A"), default)).Proposal);

        await using var db = h.CreateContext();
        Assert.Equal("7A", (await db.Boulders.SingleAsync(b => b.Id == boulder.Id)).Grade);
    }

    [Fact]
    public async Task TheCreator_MarksAVariant_AndABadListIs400_AnOutsiderGets404()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await h.BoulderService.CreateBoulderAsync(h.WallId, "Parent", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var child = await h.BoulderService.CreateBoulderAsync(h.WallId, "Child", "6B", [new BoulderHoldInput(holds[1].Id)]);

        var links = Assert.IsType<DeviceBoulderLinks>(Assert.IsType<OkObjectResult>(
            await Controller(h).SetParent(h.WallId, child.Id, new DeviceBoulderActionRequests { ParentId = parent.Id }, default)).Value);
        Assert.Equal(new DeviceBoulderLinks(child.Id, parent.Id, []), links with { VariantIds = [] });
        await using (var db = h.CreateContext())
        {
            Assert.Equal(parent.Id, (await db.Boulders.SingleAsync(b => b.Id == child.Id)).ParentBoulderId);
        }

        Refused(await Controller(h).SetVariants(h.WallId, parent.Id, new DeviceBoulderActionRequests { VariantIds = [child.Id, child.Id] }, default), 400);
        Refused(await Controller(h).SetParent(h.WallId, child.Id, new DeviceBoulderActionRequests { ParentId = child.Id }, default), 400);

        await using (var db = h.CreateContext())
        {
            var outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
            h.ActingUser = outsider;
        }

        Assert.IsType<NotFoundResult>(await Controller(h).Publish(h.WallId, parent.Id, default));
        Assert.IsType<NotFoundResult>(await Controller(h).GetProposal(h.WallId, parent.Id, default));
    }

    [Fact]
    public async Task AnAccept_OfAProposalThatChangedMeanwhile_Is409_AndAppliesNothing()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", "6A", [new BoulderHoldInput(holds[0].Id)]);
        h.ActingUser = await h.AddMemberAsync("a@test", WallRole.Member);
        var seen = State(await Controller(h).Propose(h.WallId, boulder.Id, new DeviceBoulderActionRequests { Grade = "7A" }, default)).Proposal!;
        h.ActingUser = await h.AddMemberAsync("b@test", WallRole.Member);
        Assert.Equal(seen.Id, State(await Controller(h).Propose(h.WallId, boulder.Id, new DeviceBoulderActionRequests { Grade = "7C" }, default)).Proposal!.Id);

        h.ActingUser = h.Owner;
        Refused(await Controller(h).Accept(h.WallId, boulder.Id, seen.Id, Expect("7A"), default), 409);
        await using var db = h.CreateContext();
        Assert.Equal("6A", (await db.Boulders.SingleAsync(b => b.Id == boulder.Id)).Grade);
    }

    [Fact]
    public async Task AProposalOfAnotherBoulder_Is404_AndARetriedProposalNeverPutsBackAnOlderGrade()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var first = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var second = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", "6A", [new BoulderHoldInput(holds[1].Id)]);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var key = Guid.NewGuid();
        State(await Controller(h).Propose(h.WallId, first.Id, new DeviceBoulderActionRequests { Grade = "7A", ClientRequestId = key }, default));
        var other = State(await Controller(h).Propose(h.WallId, second.Id, new DeviceBoulderActionRequests { Grade = "7B" }, default)).Proposal!;
        State(await Controller(h).Propose(h.WallId, first.Id, new DeviceBoulderActionRequests { Grade = "7C" }, default));

        var retried = State(await Controller(h).Propose(h.WallId, first.Id, new DeviceBoulderActionRequests { Grade = "7A", ClientRequestId = key }, default));
        Assert.Equal("7C", retried.Proposal!.ProposedGrade);

        h.ActingUser = h.Owner;
        Refused(await Controller(h).Accept(h.WallId, first.Id, other.Id, Expect("7B"), default), 404);
        Refused(await Controller(h).Reject(h.WallId, first.Id, other.Id, default), 404);
    }

    [Fact]
    public async Task TwoOpenProposalsForOneBoulder_AreRefusedByTheDatabase()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "B", "6A", [new BoulderHoldInput(holds[0].Id)]);
        await using var db = h.CreateContext();
        db.GradeProposals.AddRange(
            new GradeProposal { BoulderId = boulder.Id, ProposedByUserId = h.Owner.Id, ProposedGrade = "7A" },
            new GradeProposal { BoulderId = boulder.Id, ProposedByUserId = h.Owner.Id, ProposedGrade = "7B" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ALinkOntoAnotherWall_OrOntoItsOwnVariant_IsRefused()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await h.BoulderService.CreateBoulderAsync(h.WallId, "Parent", "6A", [new BoulderHoldInput(holds[0].Id)]);
        var child = await h.BoulderService.CreateBoulderAsync(h.WallId, "Child", "6B", [new BoulderHoldInput(holds[1].Id)], variantOfBoulderId: parent.Id);
        var (otherWall, otherHold) = await DeviceSetterFixture.OtherWallAsync(h, h.Owner, ownerIsMember: true);
        var foreign = await h.BoulderService.CreateBoulderAsync(otherWall, "Foreign", "6A", [new BoulderHoldInput(otherHold.Id)]);

        // Another wall's boulder is never linked: refused as another wall (400) or as not found (404), never applied.
        RefusedAny(await Controller(h).SetParent(h.WallId, parent.Id, new DeviceBoulderActionRequests { ParentId = foreign.Id }, default), 400, 404);
        RefusedAny(await Controller(h).SetVariants(h.WallId, parent.Id, new DeviceBoulderActionRequests { VariantIds = [foreign.Id] }, default), 400, 404);
        await using (var db = h.CreateContext())
        {
            Assert.Null((await db.Boulders.SingleAsync(b => b.Id == parent.Id)).ParentBoulderId);
            Assert.Null((await db.Boulders.SingleAsync(b => b.Id == foreign.Id)).ParentBoulderId);
        }

        Refused(await Controller(h).SetParent(h.WallId, parent.Id, new DeviceBoulderActionRequests { ParentId = child.Id }, default), 400);
    }

    private static void RefusedAny(IActionResult result, params int[] statuses)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Contains(refused.StatusCode ?? 0, statuses);
    }

    private static DeviceBoulderActionRequests Expect(string grade) => new() { ExpectedGrade = grade };

    private static async Task<bool> ArchivedAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return (await db.Boulders.SingleAsync(b => b.Id == id)).IsArchived;
    }

    private static DeviceGradeProposalState State(IActionResult result) =>
        Assert.IsType<DeviceGradeProposalState>(Assert.IsType<OkObjectResult>(result).Value);

    private static void Refused(IActionResult result, int status)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        Assert.IsType<ApiErrorResponse>(refused.Value);
    }

    private static DeviceBoulderActionsController Controller(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, h.BoulderService, new Services.DeviceWrites.DeviceWriteReceiptService(h.DbContextFactory))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
