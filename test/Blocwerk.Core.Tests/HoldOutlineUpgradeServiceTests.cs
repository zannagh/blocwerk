using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The "detect real outlines for existing holds" action: dry run, apply, idempotency, scope and the kill
/// switch. Outlining is faked (<see cref="EnrichmentFakes.Outlines"/>); the real outliner is exercised by
/// the HoldDetection tests.
/// </summary>
public class HoldOutlineUpgradeServiceTests
{
    [Fact]
    public async Task Preview_CountsWithoutWriting()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var before = await s.LoadHoldsAsync();

        var preview = await s.Service().PreviewAsync(h.WallId, new HoldOutlineUpgradeOptions());

        Assert.Equal(1, preview.Photos);
        Assert.Equal(3, preview.Eligible);
        Assert.Equal(2, preview.WouldOutline);
        Assert.Equal(2, preview.ByContour);
        Assert.Equal(1, preview.WouldKeepCircle);
        Assert.Equal(2, preview.WouldFingerprint);
        Assert.Contains(s.AutoOutlined, preview.ExampleHoldIds);

        var after = await s.LoadHoldsAsync();
        Assert.All(before.Values, b => AssertUnchanged(b, after[b.Id]));
        await using var db = h.CreateContext();
        Assert.False(await db.HoldOutlineUpgradeRuns.AnyAsync());
    }

    [Fact]
    public async Task Apply_WritesOnlyEligibleHolds_AndRecordsTheRun()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var before = await s.LoadHoldsAsync();

        var result = await s.Service().ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());

        Assert.Equal((3, 2, 1, 2), (result.Eligible, result.Outlined, result.KeptCircle, result.Fingerprinted));
        var after = await s.LoadHoldsAsync();

        var outlined = after[s.AutoOutlined];
        Assert.Equal(4, outlined.ShapePoints!.Count);
        Assert.Equal(HoldOutlineSource.AutoContour, outlined.OutlineSource);
        Assert.NotNull(outlined.FingerprintJson);
        var old = before[s.AutoOutlined];
        Assert.Equal((old.X, old.Y, old.Radius), (outlined.X, outlined.Y, outlined.Radius));

        var circle = after[s.AutoKeepsCircle];
        Assert.Null(circle.ShapePoints);
        Assert.Null(circle.OutlineSource);
        Assert.NotNull(circle.FingerprintJson);

        var fingerprinted = after[s.AutoWithFingerprint];
        Assert.NotNull(fingerprinted.ShapePoints);
        Assert.Equal(before[s.AutoWithFingerprint].FingerprintJson, fingerprinted.FingerprintJson);

        foreach (var untouched in new[] { s.ManualLarge, s.ManualPlaceholder, s.Virtual, s.AlreadyShaped, s.OldGeneration })
        {
            AssertUnchanged(before[untouched], after[untouched]);
        }

        await using var db = h.CreateContext();
        var run = await db.HoldOutlineUpgradeRuns.SingleAsync();
        Assert.Equal((result.RunId, h.Owner.Id, 2), (run.Id, run.CreatedByUserId, run.OutlinedCount));
    }

    [Fact]
    public async Task Apply_Twice_IsIdempotent()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var service = s.Service();
        await service.ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());
        var first = await s.LoadHoldsAsync();

        var again = await service.ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());

        // Only the hold that stayed a circle is still eligible; it already has its fingerprint now.
        Assert.Equal((1, 0, 0), (again.Eligible, again.Outlined, again.Fingerprinted));
        var second = await s.LoadHoldsAsync();
        Assert.All(first.Values, f => AssertUnchanged(f, second[f.Id]));
    }

    [Fact]
    public async Task IncludeManual_OutlinesManualHolds_ButDropsALeakFromAPlaceholderRadius()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var options = new HoldOutlineUpgradeOptions(IncludeManual: true);

        var preview = await s.Service().PreviewAsync(h.WallId, options);
        var result = await s.Service().ApplyAsync(h.WallId, options);

        Assert.Equal((5, 3, 1), (preview.Eligible, preview.WouldOutline, preview.RejectedAsLeak));
        Assert.Equal(3, result.Outlined);
        var after = await s.LoadHoldsAsync();
        Assert.NotNull(after[s.ManualLarge].ShapePoints);
        Assert.Equal(0.03, after[s.ManualLarge].Radius);
        Assert.Null(after[s.ManualPlaceholder].ShapePoints);
        Assert.Null(after[s.ManualPlaceholder].FingerprintJson);
        Assert.Equal(0.003, after[s.ManualPlaceholder].Radius);
    }

    [Fact]
    public async Task KillSwitch_RefusesToRun_AndReportsOff()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var off = s.Service(outlinesOn: false);

        Assert.False((await off.GetStatusAsync(h.WallId)).Enabled);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => off.PreviewAsync(h.WallId, new HoldOutlineUpgradeOptions()));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => off.ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions()));
        Assert.True((await s.Service().GetStatusAsync(h.WallId)).Enabled);

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.OutlineSource == HoldOutlineSource.AutoContour));
    }

    [Fact]
    public async Task Apply_NeverFlagsBoulders()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        await using (var db = h.CreateContext())
        {
            var boulder = new Boulder { WallId = h.WallId, Name = "Uses the circle", CreatedByUserId = h.Owner.Id, Generation = 1 };
            boulder.BoulderHolds.Add(new BoulderHold { HoldId = s.AutoOutlined });
            db.Boulders.Add(boulder);
            await db.SaveChangesAsync();
        }

        await s.Service().ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());

        await using var read = h.CreateContext();
        var stored = await read.Boulders.Include(b => b.BoulderHolds).SingleAsync();
        Assert.False(stored.NeedsReview || stored.IsHistoric || stored.IsArchived);
        Assert.Equal(s.AutoOutlined, stored.BoulderHolds.Single().HoldId);
        Assert.False(await read.Holds.AnyAsync(x => x.NeedsReview));
        Assert.False(await read.HoldGenerationLinks.AnyAsync());
    }

    internal static void AssertUnchanged(Hold before, Hold after)
    {
        Assert.Equal(before.ShapePoints?.Count, after.ShapePoints?.Count);
        Assert.Equal(before.ShapeHoles?.Count, after.ShapeHoles?.Count);
        Assert.Equal(before.OutlineSource, after.OutlineSource);
        Assert.Equal(before.FingerprintJson, after.FingerprintJson);
        Assert.Equal((before.X, before.Y, before.Radius), (after.X, after.Y, after.Radius));
    }
}
