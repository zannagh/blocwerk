// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Families of variants are one level deep: a parent has variants, a variant has none. These tests pin how boulders are
/// linked and reparented, who may do it, and that a family keeps a parent on the wall when its parent is retired.
/// </summary>
public class BoulderVariantTests
{
    [Fact]
    public async Task AddVariant_MakesTheOriginTheParent_AndLeavesItsHoldsAlone()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var origin = await CreateAsync(h, holds, "Origin", HoldType.Start);

        var variant = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Variant", "7A", [new BoulderHoldInput(holds[1].Id)], variantOfBoulderId: origin.Id);

        Assert.Equal(origin.Id, await ParentOfAsync(h, variant.Id));
        Assert.Null(await ParentOfAsync(h, origin.Id));
        await using var db = h.CreateContext();
        var kept = await db.BoulderHolds.Where(b => b.BoulderId == origin.Id).SingleAsync();
        Assert.Equal((holds[0].Id, HoldType.Start), (kept.HoldId, kept.Type));
    }

    [Fact]
    public async Task AddVariant_OfAVariant_LandsUnderTheParent()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var first = await VariantAsync(h, holds, "First", parent.Id);

        var second = await VariantAsync(h, holds, "Second", first.Id);

        Assert.Equal(parent.Id, await ParentOfAsync(h, second.Id));
    }

    [Fact]
    public async Task ADraftVariant_KeepsItsLink_AndJoinsTheFamilyOncePublished()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");

        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Draft", null, [new BoulderHoldInput(holds[1].Id)], isDraft: true, variantOfBoulderId: parent.Id);

        Assert.Equal(parent.Id, await ParentOfAsync(h, draft.Id));
        Assert.Empty(await h.BoulderService.GetVariantsAsync(parent.Id));

        await h.BoulderService.PublishBoulderAsync(draft.Id);

        Assert.Equal(["Draft"], (await h.BoulderService.GetVariantsAsync(parent.Id)).Select(v => v.Name));
    }

    [Fact]
    public async Task AnotherWallsBoulder_IsRefusedEveryWay()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var mine = await CreateAsync(h, holds, "Mine");
        var foreign = await SeedForeignBoulderAsync(h);

        await AssertRefusedAsync(BoulderService.VariantOtherWallMessage, () => h.BoulderService.CreateBoulderAsync(
            h.WallId, "Variant", null, [new BoulderHoldInput(holds[1].Id)], variantOfBoulderId: foreign));
        await AssertRefusedAsync(BoulderService.VariantOtherWallMessage, () => h.BoulderService.SetVariantsAsync(mine.Id, [foreign]));
        await AssertRefusedAsync(BoulderService.VariantOtherWallMessage, () => h.BoulderService.SetParentAsync(mine.Id, foreign));
    }

    [Fact]
    public async Task MakingAParentAVariant_MovesItsVariantsUnderTheNewParent()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);
        var other = await CreateAsync(h, holds, "C");

        await h.BoulderService.SetParentAsync(parent.Id, other.Id);

        Assert.Null(await ParentOfAsync(h, other.Id));
        Assert.Equal(other.Id, await ParentOfAsync(h, parent.Id));
        Assert.Equal(other.Id, await ParentOfAsync(h, variant.Id));
    }

    [Fact]
    public async Task PickingAVariantAsParent_TargetsItsParent()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);
        var loner = await CreateAsync(h, holds, "Loner");

        await h.BoulderService.SetParentAsync(loner.Id, variant.Id);

        Assert.Equal(parent.Id, await ParentOfAsync(h, loner.Id));
    }

    [Fact]
    public async Task SelfLinksAndCycles_AreRefused()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);

        await AssertRefusedAsync(BoulderService.VariantCycleMessage, () => h.BoulderService.SetParentAsync(parent.Id, parent.Id));
        await AssertRefusedAsync(BoulderService.VariantCycleMessage, () => h.BoulderService.SetParentAsync(parent.Id, variant.Id));
        await AssertRefusedAsync(BoulderService.VariantCycleMessage, () => h.BoulderService.SetVariantsAsync(parent.Id, [parent.Id]));
    }

    [Fact]
    public async Task ChoosingItsParentAsAVariant_TurnsTheFamilyAround()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);
        var sibling = await VariantAsync(h, holds, "Sibling", parent.Id);

        await h.BoulderService.SetVariantsAsync(variant.Id, [parent.Id]);

        Assert.Null(await ParentOfAsync(h, variant.Id));
        Assert.Equal(variant.Id, await ParentOfAsync(h, parent.Id));
        Assert.Equal(variant.Id, await ParentOfAsync(h, sibling.Id));
    }

    [Fact]
    public async Task MarkingFromAVariant_LinksUnderItsParent_AndLeavesTheFamilyWhole()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);
        var sibling = await VariantAsync(h, holds, "Sibling", parent.Id);
        var other = await CreateAsync(h, holds, "Other");
        var otherVariant = await VariantAsync(h, holds, "Other variant", other.Id);

        await h.BoulderService.SetVariantsAsync(variant.Id, [other.Id]);

        Assert.Equal(parent.Id, await ParentOfAsync(h, variant.Id));
        Assert.Equal(parent.Id, await ParentOfAsync(h, sibling.Id));
        Assert.Equal(parent.Id, await ParentOfAsync(h, other.Id));
        Assert.Equal(parent.Id, await ParentOfAsync(h, otherVariant.Id));
    }

    [Fact]
    public async Task UncheckingAVariant_ClearsItsParent_AndLeavesUnlistedDraftsLinked()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var kept = await VariantAsync(h, holds, "Kept", parent.Id);
        var dropped = await VariantAsync(h, holds, "Dropped", parent.Id);
        var draft = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Draft", null, [new BoulderHoldInput(holds[1].Id)], isDraft: true, variantOfBoulderId: parent.Id);

        await h.BoulderService.SetVariantsAsync(parent.Id, [kept.Id]);

        Assert.Equal(parent.Id, await ParentOfAsync(h, kept.Id));
        Assert.Null(await ParentOfAsync(h, dropped.Id));
        Assert.Equal(parent.Id, await ParentOfAsync(h, draft.Id));
    }

    [Fact]
    public async Task ADraftCannotBeMarkedAsAVariant()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var draft = await h.BoulderService.CreateBoulderAsync(h.WallId, "Draft", null, [new BoulderHoldInput(holds[1].Id)], isDraft: true);

        await AssertRefusedAsync(BoulderService.VariantNotLiveMessage, () => h.BoulderService.SetVariantsAsync(parent.Id, [draft.Id]));
    }

    [Fact]
    public async Task MarkingNeedsEditRights_AddingAVariantDoesNot()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);
        var climber = await h.AddMemberAsync("climber@test", WallRole.Member);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Boulder", null, [new BoulderHoldInput(holds[0].Id)], setterUserIds: [setter.Id]);
        var other = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Other", null, [new BoulderHoldInput(holds[0].Id)], setterUserIds: [setter.Id]);

        h.ActingUser = climber;
        await AssertRefusedAsync(BoulderService.VariantPermissionMessage, () => h.BoulderService.SetVariantsAsync(boulder.Id, [other.Id]));
        await AssertRefusedAsync(BoulderService.VariantPermissionMessage, () => h.BoulderService.SetParentAsync(boulder.Id, other.Id));
        Assert.False((await h.BoulderService.GetVariantChoicesAsync(boulder.Id))!.CanEdit);
        var added = await VariantAsync(h, holds, "By a climber", boulder.Id);
        Assert.Equal(boulder.Id, await ParentOfAsync(h, added.Id));

        h.ActingUser = setter;
        await h.BoulderService.SetVariantsAsync(boulder.Id, [other.Id, added.Id]);
        h.ActingUser = admin;
        await h.BoulderService.SetVariantsAsync(boulder.Id, [added.Id]);
        Assert.Null(await ParentOfAsync(h, other.Id));
    }

    [Fact]
    public async Task AClimberCannotMarkSomebodyElsesBoulder_AndNothingMoves()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var theirs = await CreateAsync(h, holds, "The setter's");
        var climber = await h.AddMemberAsync("climber@test", WallRole.Member);
        h.ActingUser = climber;
        var mine = await CreateAsync(h, holds, "Mine");

        await AssertRefusedAsync(BoulderService.VariantTargetPermissionMessage, () => h.BoulderService.SetVariantsAsync(mine.Id, [theirs.Id]));
        Assert.False((await h.BoulderService.GetVariantChoicesAsync(mine.Id))!.Choices.Single(c => c.Id == theirs.Id).CanMove);

        Assert.Null(await ParentOfAsync(h, theirs.Id));
        Assert.Null(await ParentOfAsync(h, mine.Id));
    }

    [Fact]
    public async Task AClimberCannotTurnSomebodyElsesFamilyAround_NorTakeAVariantOutOfIt()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "The setter's parent");
        var climber = await h.AddMemberAsync("climber@test", WallRole.Member);
        h.ActingUser = climber;
        var myVariant = await VariantAsync(h, holds, "My variant", parent.Id);
        var mine = await CreateAsync(h, holds, "Mine");

        // Picking the parent would put the setter's boulder under mine.
        await AssertRefusedAsync(BoulderService.VariantTargetPermissionMessage, () => h.BoulderService.SetVariantsAsync(myVariant.Id, [parent.Id]));
        // My own variant may move only with the family's parent's say.
        await AssertRefusedAsync(BoulderService.VariantTargetPermissionMessage, () => h.BoulderService.SetVariantsAsync(mine.Id, [myVariant.Id]));

        Assert.Null(await ParentOfAsync(h, parent.Id));
        Assert.Equal(parent.Id, await ParentOfAsync(h, myVariant.Id));
        Assert.Null(await ParentOfAsync(h, mine.Id));
    }

    [Fact]
    public async Task DeletingAParent_PromotesItsOldestPublishedVariant()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var (parent, oldest, younger) = await FamilyAsync(h, holds);

        await h.BoulderService.DeleteBoulderAsync(parent.Id);

        Assert.Null(await ParentOfAsync(h, oldest.Id));
        Assert.Equal(oldest.Id, await ParentOfAsync(h, younger.Id));
    }

    [Fact]
    public async Task AParentGoingHistoric_HandsOverAndStaysInTheFamily()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var (parent, oldest, younger) = await FamilyAsync(h, holds);

        await RetireAsync(h, parent.Id);

        Assert.Null(await ParentOfAsync(h, oldest.Id));
        Assert.Equal(oldest.Id, await ParentOfAsync(h, younger.Id));
        Assert.Equal(oldest.Id, await ParentOfAsync(h, parent.Id));
    }

    [Fact]
    public async Task ArchivingAParent_HandsOverToo()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var (parent, oldest, younger) = await FamilyAsync(h, holds);
        await using (var db = h.CreateContext())
        {
            // As a full wall revision retires a boulder: archived in one step.
            var row = await db.Boulders.SingleAsync(b => b.Id == parent.Id);
            (row.IsHistoric, row.IsArchived) = (true, true);
            await db.SaveChangesAsync();
        }

        Assert.Null(await ParentOfAsync(h, oldest.Id));
        Assert.Equal(oldest.Id, await ParentOfAsync(h, younger.Id));
    }

    [Fact]
    public async Task AFamilyWithNoVariantOnTheWall_WaitsForTheFirstOneBack()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);
        await RetireAsync(h, variant.Id);
        await RetireAsync(h, parent.Id);
        Assert.Equal(parent.Id, await ParentOfAsync(h, variant.Id));

        await h.BoulderService.ReviseBoulderAsync(variant.Id, [new BoulderHoldInput(holds[1].Id)]);

        Assert.Null(await ParentOfAsync(h, variant.Id));
        Assert.Equal(variant.Id, await ParentOfAsync(h, parent.Id));
    }

    [Fact]
    public async Task ARevision_KeepsTheLinks()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var parent = await CreateAsync(h, holds, "Parent");
        var variant = await VariantAsync(h, holds, "Variant", parent.Id);

        await h.BoulderService.ReviseBoulderAsync(parent.Id, [new BoulderHoldInput(holds[2].Id)]);
        await h.BoulderService.ReviseBoulderAsync(variant.Id, [new BoulderHoldInput(holds[3].Id)]);

        Assert.Null(await ParentOfAsync(h, parent.Id));
        Assert.Equal(parent.Id, await ParentOfAsync(h, variant.Id));
    }

    [Fact]
    public async Task TheCard_ListsVariantsOnAParent_AndTheMarkedParentAndSiblingsOnAVariant()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var (parent, oldest, younger) = await FamilyAsync(h, holds);

        var onParent = await h.BoulderService.GetVariantsAsync(parent.Id);
        var onVariant = await h.BoulderService.GetVariantsAsync(younger.Id);

        Assert.Equal([oldest.Id, younger.Id], onParent.Select(v => v.Id));
        Assert.DoesNotContain(onParent, v => v.IsParent);
        Assert.Equal([parent.Id, oldest.Id], onVariant.Select(v => v.Id));
        Assert.True(onVariant[0].IsParent);
    }

    private static Task<Boulder> CreateAsync(WallTestHarness h, List<Hold> holds, string name, HoldType type = HoldType.Normal) =>
        h.BoulderService.CreateBoulderAsync(h.WallId, name, "6A", [new BoulderHoldInput(holds[0].Id, type)]);

    private static Task<Boulder> VariantAsync(WallTestHarness h, List<Hold> holds, string name, Guid originId) =>
        h.BoulderService.CreateBoulderAsync(h.WallId, name, "6B", [new BoulderHoldInput(holds[1].Id)], variantOfBoulderId: originId);

    // A parent and two variants published a day apart, the first one being the older.
    private static async Task<(Boulder Parent, Boulder Oldest, Boulder Younger)> FamilyAsync(WallTestHarness h, List<Hold> holds)
    {
        var parent = await CreateAsync(h, holds, "Parent");
        var younger = await VariantAsync(h, holds, "B younger", parent.Id);
        var oldest = await VariantAsync(h, holds, "A oldest", parent.Id);
        await using var db = h.CreateContext();
        var row = await db.Boulders.SingleAsync(b => b.Id == oldest.Id);
        row.PublishedAt = younger.PublishedAt!.Value.AddDays(-1);
        await db.SaveChangesAsync();
        return (parent, oldest, younger);
    }

    private static async Task RetireAsync(WallTestHarness h, Guid boulderId)
    {
        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == boulderId);
        boulder.IsHistoric = true;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid?> ParentOfAsync(WallTestHarness h, Guid boulderId)
    {
        await using var db = h.CreateContext();
        return await db.Boulders.Where(b => b.Id == boulderId).Select(b => b.ParentBoulderId).SingleAsync();
    }

    private static async Task<Guid> SeedForeignBoulderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.CurrentUserId = Guid.Empty;
        var wall = new Wall { Name = "Foreign", OwnerId = h.Owner.Id, Photo = [1], PhotoContentType = "image/jpeg" };
        var boulder = new Boulder { WallId = wall.Id, Name = "Foreign", CreatedByUserId = h.Owner.Id };
        db.Walls.Add(wall);
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static async Task AssertRefusedAsync(string message, Func<Task> action)
    {
        var refusal = await Assert.ThrowsAsync<UserFacingException>(action);
        Assert.Equal(message, refusal.Message);
    }
}
