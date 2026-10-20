// <copyright file="InsideCarriedHoldServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// Through the real session (<c>ResumeAsync</c>): an unpaired detection inside the footprint of a carried hold is a second
/// outline or a part of it, suggested as discarded with <see cref="NewHoldDiscardReason.InsideCarriedHold"/> and written onto
/// the staged hold, whether the old hold was found again or carried where the warp puts it. When the look check finds the
/// old hold gone from its spot, the detection there is what is there now and stays. On SQLite and, with
/// <see cref="PostgresTestDatabase.EnvVar"/> set, on PostgreSQL.
/// </summary>
public class InsideCarriedHoldServiceTests
{
    // Inside the target's footprint (radius 0.03 at 0.6 / 0.5) but too far from it to be paired with it.
    private const double InsideX = 0.615;

    private const string Orange = """{"version":1,"l":76,"a":143,"b":146}""";
    private const string Purple = """{"version":1,"l":130,"a":139,"b":111}""";

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADetectionInsideARefoundHold_IsDiscardedAsInsideIt(bool postgres)
    {
        using var h = Harness(postgres);
        var w = await SeedAsync(h, targetDetected: true);
        var extra = await AddDetectionAsync(h, w);
        w.Probe.Set(InsideX, TargetY, 0.1);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Equal(NewHoldDiscardReason.InsideCarriedHold, session.SuggestedNewDiscards![extra]);
        await using var db = h.CreateContext();
        Assert.Equal(nameof(NewHoldDiscardReason.InsideCarriedHold), (await db.Holds.SingleAsync(x => x.Id == extra)).DiscardReason);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADiscardedDetection_OutlivesTheApply_AsALookOverFindingThatCanStillBeAdded(bool postgres)
    {
        using var h = Harness(postgres);
        var w = await SeedAsync(h, targetDetected: true);
        var extra = await AddDetectionAsync(h, w);
        w.Probe.Set(InsideX, TargetY, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);

        var quick = await RecordAsync(w, session);
        await PromoteAsync(w, session);

        Assert.Contains(extra, quick.RemovedNewCentreHoldIds);
        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.Id == extra));
        var finding = await db.WallLookOverFindings.SingleAsync(f => f.WallId == w.WallId && f.Kind == LookOverFindingKind.DiscardedDetection);
        Assert.Equal((nameof(NewHoldDiscardReason.InsideCarriedHold), LookOverFindingStatus.Open), (finding.DiscardReason, finding.Status));
        Assert.Equal(InsideX, finding.X, 6);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADetectionInsideAnOldHoldCarriedByTheWarp_IsDiscarded_WhileTheOldHoldStillShows(bool postgres)
    {
        using var h = Harness(postgres);
        var w = await SeedAsync(h, targetDetected: false);
        var extra = await AddDetectionAsync(h, w);
        w.Probe.Set(InsideX, TargetY, 0.1);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Equal(NewHoldDiscardReason.InsideCarriedHold, session.SuggestedNewDiscards![extra]);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADetectionWhereTheOldHoldNoLongerShows_IsWhatIsThereNow_AndStays(bool postgres)
    {
        using var h = Harness(postgres);
        var w = await SeedAsync(h, targetDetected: false);
        var extra = await AddDetectionAsync(h, w);
        w.Probe.Set(TargetX, TargetY, 0.05);

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.Contains(w.TargetId, session.NotFoundAgain!.Keys);
        Assert.False(session.SuggestedNewDiscards!.ContainsKey(extra));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADetectionOfAnotherColourInsideAnOldHold_IsKept_AndTheOldHoldRecordedAsReplaced(bool postgres)
    {
        using var h = Harness(postgres);
        var w = await SeedAsync(h, targetDetected: false);
        var extra = await AddDetectionAsync(h, w, Purple, x: 0.612, radius: 0.024);
        w.Probe.Set(0.612, TargetY, 0.3);
        await using (var db = h.CreateContext())
        {
            (await db.Holds.SingleAsync(x => x.Id == w.TargetId)).FingerprintJson = Orange;
            await db.SaveChangesAsync();
        }

        var session = await w.Service.ResumeAsync(w.WallId);

        Assert.False(session.SuggestedNewDiscards!.ContainsKey(extra));
        Assert.Equal(HoldMoveOutcome.Replaced, session.NotFoundAgain![w.TargetId]);
        Assert.Contains(session.Looks!, f => f.OldHoldId == w.TargetId && f.NewHoldId == extra && f.Look == HoldLook.Replaced);
    }

    private static WallTestHarness Harness(bool postgres)
    {
        Skip.If(postgres && !PostgresTestDatabase.Enabled, PostgresTestDatabase.SkipReason);
        return postgres ? new WallTestHarness(PostgresTestDatabase.Create()) : new WallTestHarness();
    }

    /// <summary>A small unpaired detection inside the target's footprint on the staged centre.</summary>
    private static async Task<Guid> AddDetectionAsync(
        WallTestHarness h, LookWall w, string? fingerprint = null, double x = InsideX, double radius = 0.012)
    {
        await using var db = h.CreateContext();
        var staged = await db.WallPanels.SingleAsync(p => p.WallId == w.WallId && p.Generation == 3 && p.Col == 0);
        var hold = new Hold
        {
            WallId = w.WallId, WallPanelId = staged.Id, X = x, Y = TargetY, Radius = radius, IsAutoDetected = true, Confidence = 0.8,
            NeedsReview = true, Generation = 3, FingerprintJson = fingerprint,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        w.StagedByPanel[staged.Id] = [.. w.StagedByPanel[staged.Id], hold.Id];
        return hold.Id;
    }
}
