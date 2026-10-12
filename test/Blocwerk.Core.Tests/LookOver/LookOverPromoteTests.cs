// <copyright file="LookOverPromoteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// What a promote leaves behind: every finding nobody answered is kept with the wall, tied to the live hold the old one
/// became, and the wall is flagged. A hold a person decided before the update is no finding, and an update where nothing
/// is unsure flags nothing.
/// </summary>
public class LookOverPromoteTests
{
    [Fact]
    public async Task AReplacedHoldNobodyAnswered_BecomesAFinding_AndFlagsTheWall()
    {
        using var h = new WallTestHarness();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);

        var wall = await WallAsync(h, w.WallId);
        Assert.True(wall.LookOverPendingSince >= before);
        Assert.Equal((3, (DateTimeOffset?)null), (wall.LookOverGeneration, wall.LookOverCompletedAt));
        var finding = await FindingAsync(h, w.WallId);
        var kept = (await BoulderAsync(h, w.BoulderId)).BoulderHolds.Single().HoldId;
        Assert.Equal(
            (LookOverFindingKind.ReplacedByOther, LookOverFindingStatus.Open, 3, (Guid?)w.TargetId, (Guid?)kept, w.DetectionId, 1),
            (finding.Kind, finding.Status, finding.Generation, finding.OldHoldId, finding.HoldId, finding.OtherHoldId, finding.BouldersAtApply));
        Assert.Equal(((Guid?)w.SessionId, TargetX, TargetY, (double?)0.1), (finding.SessionId, finding.X, finding.Y, finding.PhotoScore));

        // The card asked about the old hold and nobody answered: its copy was marked, by this finding alone.
        Assert.True((await HoldAsync(h, kept))!.NeedsReview);
        Assert.True(finding.FlaggedHold);
    }

    [Fact]
    public async Task AnUnsureMatch_IsAFindingAboutTheDetectionTheHoldWasCarriedOnto()
    {
        using var h = new WallTestHarness();

        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal(
            (LookOverFindingKind.UnsureMatch, (Guid?)w.TargetId, w.DetectionId, (Guid?)null, (double?)0.4, 1),
            (finding.Kind, finding.OldHoldId, finding.HoldId, finding.OtherHoldId, finding.PhotoScore, finding.BouldersAtApply));
        Assert.NotNull((await WallAsync(h, w.WallId)).LookOverPendingSince);
    }

    [Fact]
    public async Task AHoldOnABoulderThatWasNotFoundAgain_IsAFinding_ThoughItNeverHadACard()
    {
        using var h = new WallTestHarness();

        var w = await ApplyAsync(h, targetDetected: false, score: 0.2);

        var finding = await FindingAsync(h, w.WallId);
        var boulder = await BoulderAsync(h, w.BoulderId);
        Assert.Equal(
            (LookOverFindingKind.NotFound, (Guid?)w.TargetId, (Guid?)boulder.BoulderHolds.Single().HoldId, 1),
            (finding.Kind, finding.OldHoldId, finding.HoldId, finding.BouldersAtApply));
        Assert.True(boulder.NeedsReview);
    }

    [Theory]
    [InlineData(true, 0.9)]
    [InlineData(false, 0.9)]
    public async Task AnUpdateWithNothingUnsure_LeavesNoFinding_AndNoFlag(bool targetDetected, double score)
    {
        using var h = new WallTestHarness();

        var w = await ApplyAsync(h, targetDetected, score);

        await using var db = h.CreateContext();
        Assert.Empty(await db.WallLookOverFindings.ToListAsync());
        var wall = await WallAsync(h, w.WallId);
        Assert.Null(wall.LookOverPendingSince);
        Assert.Null(wall.LookOverGeneration);
    }

    [Theory]
    [InlineData(UpdateExceptionAnswer.Keep)]
    [InlineData(UpdateExceptionAnswer.Remove)]
    public async Task ACardAPersonAnsweredBeforeTheUpdate_IsNoFinding(UpdateExceptionAnswer answer)
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        var card = Assert.Single(await w.Sessions.GetUpdateExceptionsAsync(w.WallId));
        await w.Sessions.DecideUpdateExceptionAsync(w.WallId, card.Id, answer);

        await PromoteLikeApplyAsync(w, session);

        await using var db = h.CreateContext();
        Assert.Empty(await db.WallLookOverFindings.ToListAsync());
        Assert.Null((await WallAsync(h, w.WallId)).LookOverPendingSince);
    }

    [Fact]
    public async Task TheStepByStepPromote_WithoutTheCardsMarks_StillKeepsTheFindings()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        w.Probe.Set(TargetX, TargetY, 0.4);
        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);

        // The full review's promote names no holds to mark: the finding is there all the same, it just owns no mark.
        await PromoteAsync(w, session);

        var finding = await FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingKind.UnsureMatch, false), (finding.Kind, finding.FlaggedHold));
        Assert.NotNull((await WallAsync(h, w.WallId)).LookOverPendingSince);
    }

    [Fact]
    public async Task AnOpenFindingOnAPanelThatIsPhotographedAgain_IsSuperseded_NotCarriedAlong()
    {
        using var h = new WallTestHarness();
        var w = await SeedAsync(h, targetDetected: true);
        Guid earlier;
        await using (var db = h.CreateContext())
        {
            var livePanel = await db.WallPanels.SingleAsync(p => p.WallId == w.WallId && p.Photo != null);
            var finding = new WallLookOverFinding { WallId = w.WallId, Generation = 2, Kind = LookOverFindingKind.UnsureMatch, PanelId = livePanel.Id };
            db.WallLookOverFindings.Add(finding);
            await db.SaveChangesAsync();
            earlier = finding.Id;
        }

        var session = await w.Service.ResumeAsync(w.WallId);
        await RecordAsync(w, session);
        await PromoteLikeApplyAsync(w, session);

        await using var after = h.CreateContext();
        Assert.Equal(LookOverFindingStatus.Superseded, (await after.WallLookOverFindings.SingleAsync(f => f.Id == earlier)).Status);
        Assert.Null((await WallAsync(h, w.WallId)).LookOverPendingSince);
    }
}
