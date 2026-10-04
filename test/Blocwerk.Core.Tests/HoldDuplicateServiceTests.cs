// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>The duplicate-hold review: listing, merging (boulders, roles, flags), exact undo, dismissals and the write lock.</summary>
public sealed class HoldDuplicateServiceTests : IDisposable
{
    private readonly WallTestHarness harness = new();

    public void Dispose() => harness.Dispose();

    [Fact]
    public async Task ListFindsThePairsAndRanksThemByConfidence_AndPages()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var manual = await s.AddHoldAsync(0.3, 0.3, 0.05, auto: false);
        await s.AddHoldAsync(0.3, 0.3, 0.02, auto: true);
        await s.AddHoldAsync(0.7, 0.7, 0.04, auto: true, color: "red");
        await s.AddHoldAsync(0.702, 0.7, 0.04, auto: true, color: "red");
        await s.AddHoldAsync(0.5, 0.9, 0.02, auto: true);
        await s.AddHoldAsync(0.6, 0.9, 0.02, auto: true);

        var page = await s.Service().ListAsync(harness.WallId, 0, 20);

        Assert.Equal(2, page.Total);
        Assert.Equal(1, page.ByKind[HoldDuplicateKind.InsideHandPlaced]);
        Assert.Equal(1, page.ByKind[HoldDuplicateKind.NearDuplicateAutomatic]);
        Assert.True(page.Items[0].Confidence >= page.Items[1].Confidence);
        Assert.Contains(page.Items, i => i.Left.Id == manual && i.SuggestedMode == HoldMergeMode.KeepHandUseDetectedShape);
        Assert.Single((await s.Service().ListAsync(harness.WallId, 1, 1)).Items);
    }

    [Fact]
    public async Task MergeMovesBouldersKeepsTheMoreProminentRoleAndDeletesTheRemovedHold()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.3, 0.3, 0.05, auto: false, name: "Big");
        var removed = await s.AddHoldAsync(0.3, 0.3, 0.02, auto: true, color: "red");
        var onlyRemoved = await s.AddBoulderAsync("OnlyRemoved", (removed, HoldType.Top));
        var both = await s.AddBoulderAsync("Both", (kept, HoldType.Start), (removed, HoldType.Top));
        var bothSameRole = await s.AddBoulderAsync("BothPlain", (kept, HoldType.Normal), (removed, HoldType.Normal));
        var onlyKept = await s.AddBoulderAsync("OnlyKept", (kept, HoldType.Normal));

        var result = await s.Service().MergeAsync(harness.WallId, kept, removed, HoldMergeMode.KeepLeft);

        Assert.Equal((1, 2), (result.MovedBoulders, result.FlaggedForReview));
        var holds = await s.HoldsAsync();
        Assert.False(holds.ContainsKey(removed));
        Assert.Equal("red", holds[kept].Color);
        Assert.Equal(HoldType.Top, (await s.LinksAsync(onlyRemoved)).Single().Type);
        Assert.Equal(kept, (await s.LinksAsync(onlyRemoved)).Single().HoldId);
        Assert.Equal(HoldType.Top, (await s.LinksAsync(both)).Single().Type);
        Assert.Single(await s.LinksAsync(bothSameRole));
        Assert.True(await s.NeedsReviewAsync(both));
        Assert.True(await s.NeedsReviewAsync(bothSameRole));
        Assert.False(await s.NeedsReviewAsync(onlyRemoved));
        Assert.False(await s.NeedsReviewAsync(onlyKept));
    }

    [Fact]
    public async Task MergeCarriesTheUsefulFlagsAndProvenance()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.3, 0.3, 0.04, auto: true);
        var removed = await s.AddHoldAsync(0.3, 0.3, 0.02, auto: false, name: "Mine");
        await using (var db = harness.CreateContext())
        {
            var hold = db.Holds.Single(h => h.Id == removed);
            hold.IsOnKickboard = true;
            hold.HandType = HoldHandType.Crimp;
            await db.SaveChangesAsync();
        }

        await s.Service().MergeAsync(harness.WallId, kept, removed, HoldMergeMode.KeepLeft);

        var merged = (await s.HoldsAsync())[kept];
        Assert.Equal(("Mine", true, HoldHandType.Crimp, false), (merged.Name, merged.IsOnKickboard, merged.HandType, merged.IsAutoDetected));
    }

    [Fact]
    public async Task UndoRestoresEverythingExactly()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var kept = await s.AddHoldAsync(0.3, 0.3, 0.05, auto: false);
        var removed = await s.AddHoldAsync(0.3, 0.3, 0.02, auto: true, color: "red");
        var both = await s.AddBoulderAsync("Both", (kept, HoldType.Start), (removed, HoldType.Top));
        var only = await s.AddBoulderAsync("Only", (removed, HoldType.Normal));
        var before = await s.HoldsAsync();
        var linksBefore = (await s.LinksAsync(both)).Select(l => (l.HoldId, l.Type)).OrderBy(l => l.HoldId).ToList();

        var merged = await s.Service().MergeAsync(harness.WallId, kept, removed, HoldMergeMode.KeepLeft);
        var reverted = await s.Service().RevertAsync(harness.WallId, merged.BatchId);

        Assert.True(reverted.Reverted, reverted.Error);
        var after = await s.HoldsAsync();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        Assert.Equal((before[kept].Color, before[kept].Name), (after[kept].Color, after[kept].Name));
        Assert.Equal((before[removed].Radius, before[removed].Color), (after[removed].Radius, after[removed].Color));
        Assert.Equal(linksBefore, (await s.LinksAsync(both)).Select(l => (l.HoldId, l.Type)).OrderBy(l => l.HoldId).ToList());
        Assert.Equal(removed, (await s.LinksAsync(only)).Single().HoldId);
        Assert.False(await s.NeedsReviewAsync(both));
        Assert.Empty((await s.Service().ListAsync(harness.WallId, 0, 20)).RecentMerges);
    }

    [Fact]
    public async Task ADismissedPairIsNotSuggestedAgain()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var a = await s.AddHoldAsync(0.3, 0.3, 0.05, auto: false);
        var b = await s.AddHoldAsync(0.3, 0.3, 0.02, auto: true);
        Assert.Equal(1, (await s.Service().ListAsync(harness.WallId, 0, 20)).Total);

        await s.Service().DismissAsync(harness.WallId, b, a);
        await s.Service().DismissAsync(harness.WallId, a, b);

        Assert.Equal(0, (await s.Service().ListAsync(harness.WallId, 0, 20)).Total);
    }

    [Fact]
    public async Task AMergeTakesTheWallHoldWriteLock()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var a = await s.AddHoldAsync(0.3, 0.3, 0.05, auto: false);
        var b = await s.AddHoldAsync(0.3, 0.3, 0.02, auto: true);

        using (WallHoldWriteLock.TryAcquire(harness.WallId, "busy"))
        {
            await Assert.ThrowsAsync<UserFacingException>(() => s.Service().MergeAsync(harness.WallId, a, b, HoldMergeMode.KeepLeft));
        }

        Assert.Contains(b, (await s.HoldsAsync()).Keys);
        await s.Service().MergeAsync(harness.WallId, a, b, HoldMergeMode.KeepLeft);
        Assert.DoesNotContain(b, (await s.HoldsAsync()).Keys);
    }

    [Fact]
    public async Task AVirtualHoldAgainstADetectionKeepsItsIdentityAdoptsTheShapeAndBecomesReal()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var virt = await s.AddHoldAsync(0.31, 0.3, 0.03, auto: false, isVirtual: true, name: "Black ring");
        var shape = HoldShapeSmootherTests.Blob(0.04, 0.04, 24).ToList();
        var detected = await s.AddHoldAsync(0.3, 0.3, 0.04, auto: true, shape: shape);
        var boulder = await s.AddBoulderAsync("Uses virtual", (virt, HoldType.Start));

        var item = (await s.Service().ListAsync(harness.WallId, 0, 20)).Items.Single();
        Assert.Equal((virt, HoldMergeMode.KeepHandUseDetectedShape), (item.Left.Id, item.SuggestedMode));
        var result = await s.Service().MergeAsync(harness.WallId, item.Left.Id, item.Right.Id, item.SuggestedMode);

        Assert.Equal((virt, detected), (result.KeptId, result.RemovedId));
        var holds = await s.HoldsAsync();
        var merged = holds[virt];
        Assert.False(holds.ContainsKey(detected));
        Assert.Equal(("Black ring", false, 0.3, 0.04, shape.Count), (merged.Name, merged.IsVirtual, merged.X, merged.Radius, merged.ShapePoints!.Count));
        Assert.Equal(HoldOutlineSource.AutoContour, merged.OutlineSource);
        var link = (await s.LinksAsync(boulder)).Single();
        Assert.Equal((virt, HoldType.Start), (link.HoldId, link.Type));
        Assert.False(await s.NeedsReviewAsync(boulder));
    }

    [Fact]
    public async Task TheDetectedShapeModeIsRefusedWhenBothHoldsAreAutomatic()
    {
        var s = await HoldDuplicateScenario.CreateAsync(harness);
        var a = await s.AddHoldAsync(0.3, 0.3, 0.04, auto: true);
        var b = await s.AddHoldAsync(0.3, 0.3, 0.04, auto: true);

        await Assert.ThrowsAsync<UserFacingException>(() => s.Service().MergeAsync(harness.WallId, a, b, HoldMergeMode.KeepHandUseDetectedShape));
    }
}
