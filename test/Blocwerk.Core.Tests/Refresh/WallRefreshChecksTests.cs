// <copyright file="WallRefreshChecksTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The confirm screen's cards: an answer is written as the full review's decision and the summary is made again, so
/// its version covers the answer and Apply promotes exactly what the screen shows; a "possibly removed" hold is never
/// removed unless a person says so.
/// </summary>
public class WallRefreshChecksTests
{
    [Fact]
    public async Task ACardWrittenAfterTheSummary_IsNotAppliedUnseen()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await AddCardAsync(h, view, old[0].Id);

        await s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion);
        await s.RunQueuedAsync();

        var back = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToApply, back.Status);
        Assert.NotEqual(view.Summary.DecisionsVersion, back.Summary!.DecisionsVersion);
        Assert.Equal(1, back.Summary.PossiblyRemovedHolds);
        Assert.Equal(0, await GenerationAsync(h));
    }

    [Fact]
    public async Task AnUnansweredCard_KeepsTheHold_AndNeverRemovesIt()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (view, _) = await PrepareWithCardAsync(h, s, old[0].Id);

        Assert.Equal(1, view.Summary!.ChecksOpen);
        await s.Service.ApplyAsync(view.Id, view.Summary.DecisionsVersion);
        await s.RunQueuedAsync();

        Assert.Equal(WallRefreshStatus.Done, (await s.CurrentAsync()).Status);
        Assert.Single(await CarriedAsync(h, old[0].Id));
        Assert.Single(await CarriedAsync(h, old[1].Id));
    }

    [Fact]
    public async Task AKeptCard_ChangesTheVersion_AndTheHoldGoesLiveConfirmed()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (pending, card) = await PrepareWithCardAsync(h, s, old[0].Id);

        var kept = await AnswerAsync(s, pending.Id, card, UpdateExceptionAnswer.Keep);

        Assert.NotEqual(pending.Summary!.DecisionsVersion, kept.Summary!.DecisionsVersion);
        Assert.Equal(0, kept.Summary.ChecksOpen);
        await s.Service.ApplyAsync(kept.Id, kept.Summary.DecisionsVersion);
        await s.RunQueuedAsync();
        Assert.Single(await CarriedAsync(h, old[0].Id));
    }

    [Fact]
    public async Task ARemovedCard_ChangesTheVersion_AndTheHoldIsGone()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (pending, card) = await PrepareWithCardAsync(h, s, old[0].Id);
        var kept = await AnswerAsync(s, pending.Id, card, UpdateExceptionAnswer.Keep);

        var removed = await AnswerAsync(s, pending.Id, card, UpdateExceptionAnswer.Remove);

        Assert.NotEqual(pending.Summary!.DecisionsVersion, removed.Summary!.DecisionsVersion);
        Assert.NotEqual(kept.Summary!.DecisionsVersion, removed.Summary.DecisionsVersion);
        Assert.Equal(1, removed.Summary.Removed);
        await s.Service.ApplyAsync(removed.Id, removed.Summary.DecisionsVersion);
        await s.RunQueuedAsync();
        Assert.Empty(await CarriedAsync(h, old[0].Id));
        Assert.Single(await CarriedAsync(h, old[1].Id));
    }

    [Fact]
    public void TheFingerprint_CoversTheHoldsMarkedForReview_AndTheAnswers()
    {
        var hold = Guid.NewGuid();
        var kept = new BigUpdateConfirmation([new CarryoverDecision(hold, CarryKind.Carried, null, true)], [], [], []);
        var unanswered = kept with { Carryover = [new CarryoverDecision(hold, CarryKind.Carried, null, false)], ReviewOldHoldIds = [hold] };
        var removed = kept with { Carryover = [new CarryoverDecision(hold, CarryKind.Removed, null, true)] };
        var staged = new Dictionary<Guid, IReadOnlyList<Guid>>();

        var versions = new[] { kept, unanswered, removed, unanswered with { ReviewOldHoldIds = [] } }
            .Select(c => RefreshDecisions.Fingerprint(c, staged)).ToList();

        Assert.Equal(4, versions.Distinct().Count());
        Assert.Equal(versions[1], RefreshDecisions.Fingerprint(unanswered with { ReviewOldHoldIds = new HashSet<Guid> { hold } }, staged));
    }

    [Fact]
    public async Task UndoingAnAnswer_ReturnsToThePendingVersion()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (pending, card) = await PrepareWithCardAsync(h, s, old[0].Id);
        await AnswerAsync(s, pending.Id, card, UpdateExceptionAnswer.Remove);

        var undone = await AnswerAsync(s, pending.Id, card, UpdateExceptionAnswer.Undo);

        Assert.Equal(pending.Summary!.DecisionsVersion, undone.Summary!.DecisionsVersion);
    }

    [Fact]
    public async Task WhileTheSummaryIsMadeAgain_ApplyIsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (pending, card) = await PrepareWithCardAsync(h, s, old[0].Id);

        await s.Service.DecideCheckAsync(pending.Id, card, UpdateExceptionAnswer.Remove);

        Assert.True((await s.CurrentAsync()).SummaryUpdating);
        await Assert.ThrowsAsync<UserFacingException>(() => s.Service.ApplyAsync(pending.Id, pending.Summary!.DecisionsVersion));
        await s.RunQueuedAsync();
        Assert.False((await s.CurrentAsync()).SummaryUpdating);
    }

    [Fact]
    public async Task ACardsPicture_IsMadeOnRequestAndCached()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (view, card) = await PrepareWithCardAsync(h, s, old[0].Id);

        // Nothing was made in advance: a photo that decodes only now is what the first request shows.
        await SetOldPhotoAsync(h, CaptureScenario.TinyJpeg(seed: 3));
        var first = await s.Service.GetCheckCropAsync(view.Id, card, CheckCropView.Old, CancellationToken.None);
        await SetOldPhotoAsync(h, null);
        var second = await s.Service.GetCheckCropAsync(view.Id, card, CheckCropView.Old, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(0xFF, first![0]);
        Assert.Equal(0xD8, first[1]);
    }

    [Fact]
    public async Task APictureThatFailedToRead_IsNotRemembered_ButTriedAgain()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (view, card) = await PrepareWithCardAsync(h, s, old[0].Id);

        // The seeded photo does not decode: a failure, not "no picture by design".
        Assert.Null(await s.Service.GetCheckCropAsync(view.Id, card, CheckCropView.Old, CancellationToken.None));
        await SetOldPhotoAsync(h, CaptureScenario.TinyJpeg(seed: 5));
        var again = await s.Service.GetCheckCropAsync(view.Id, card, CheckCropView.Old, CancellationToken.None);

        Assert.NotNull(again);
    }

    [Fact]
    public async Task ConcurrentRequestsForOnePicture_AllGetIt()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (view, card) = await PrepareWithCardAsync(h, s, old[0].Id);
        await SetOldPhotoAsync(h, CaptureScenario.TinyJpeg(seed: 6));

        var all = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => s.Service.GetCheckCropAsync(view.Id, card, CheckCropView.Old, CancellationToken.None)));

        Assert.All(all, bytes => Assert.Equal(all[0], bytes));
        Assert.NotNull(all[0]);
    }

    [Fact]
    public async Task ALostSummaryRequest_StopsHoldingApplyBackAfterTenMinutes()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (pending, card) = await PrepareWithCardAsync(h, s, old[0].Id);
        await s.Service.DecideCheckAsync(pending.Id, card, UpdateExceptionAnswer.Keep);
        await s.Queue.DequeueAsync(CancellationToken.None);

        // Fresh: Apply waits. Stale (the job was lost): Apply goes on, and the version check still guards it.
        Assert.True(WallRefreshProcessor.IsResummarizing(await RefreshRowAsync(h, pending.Id), DateTimeOffset.UtcNow));
        await SetRequestedAtAsync(h, pending.Id, DateTimeOffset.UtcNow - WallRefreshProcessor.ResummarizeStale - TimeSpan.FromMinutes(1));
        Assert.False((await s.CurrentAsync()).SummaryUpdating);
        await s.Service.ApplyAsync(pending.Id, pending.Summary!.DecisionsVersion);

        Assert.Null((await RefreshRowAsync(h, pending.Id)).SummaryRequestedAt);
        Assert.Equal(WallRefreshStatus.Applying, (await RefreshRowAsync(h, pending.Id)).Status);
    }

    private static async Task<WallRefresh> RefreshRowAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallRefreshes.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task SetRequestedAtAsync(WallTestHarness h, Guid id, DateTimeOffset at)
    {
        await using var db = h.CreateContext();
        var row = await db.WallRefreshes.SingleAsync(r => r.Id == id);
        row.SummaryRequestedAt = at;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ACardsMissingPicture_IsNull_AndTheModelPictureNeedsATexture()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var (view, card) = await PrepareWithCardAsync(h, s, old[0].Id);

        Assert.Null(await s.Service.GetCheckCropAsync(view.Id, card, CheckCropView.Model, CancellationToken.None));
        Assert.Null(await s.Service.GetCheckCropAsync(view.Id, Guid.NewGuid(), CheckCropView.Old, CancellationToken.None));
        Assert.False((await s.Service.GetChecksAsync(view.Id)).Single().HasModel);
    }

    private static async Task<(WallRefreshView View, Guid Card)> PrepareWithCardAsync(WallTestHarness h, RefreshScenario s, Guid oldHoldId)
    {
        var prepared = await s.PrepareAsync();
        var card = await AddCardAsync(h, prepared, oldHoldId);

        // A card written with the defaults is in the summary; here it is added, then the summary is made again.
        await s.Service.DecideCheckAsync(prepared.Id, card, UpdateExceptionAnswer.Keep);
        await s.RunQueuedAsync();
        return (await AnswerAsync(s, prepared.Id, card, UpdateExceptionAnswer.Undo), card);
    }

    private static async Task<WallRefreshView> AnswerAsync(RefreshScenario s, Guid refreshId, Guid card, UpdateExceptionAnswer answer)
    {
        await s.Service.DecideCheckAsync(refreshId, card, answer);
        await s.RunQueuedAsync();
        return await s.CurrentAsync();
    }

    private static async Task<Guid> AddCardAsync(WallTestHarness h, WallRefreshView view, Guid oldHoldId)
    {
        await using var db = h.CreateContext();
        var panel = await db.WallPanels.AsNoTracking().FirstAsync(p => p.WallId == h.WallId && p.StagedPhoto != null);
        var card = new WallUpdateException
        {
            SessionId = view.UpdateSessionId!.Value,
            Kind = UpdateExceptionKind.PossiblyRemoved,
            OldHoldId = oldHoldId,
            PanelId = panel.Id,
            X = 0.5,
            Y = 0.5,
            PhotoScore = 0.05,
            TextureScore = 0.1,
        };
        db.WallUpdateExceptions.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }

    private static async Task SetOldPhotoAsync(WallTestHarness h, byte[]? photo)
    {
        await using var db = h.CreateContext();
        var live = await db.WallPanels.FirstAsync(p => p.WallId == h.WallId && p.Generation == 0 && p.Col == 0 && p.Row == 0);
        live.Photo = photo;
        await db.SaveChangesAsync();
    }

    private static async Task<List<Hold>> CarriedAsync(WallTestHarness h, Guid oldHoldId)
    {
        await using var db = h.CreateContext();
        return await db.HoldGenerationLinks.AsNoTracking()
            .Where(l => l.WallId == h.WallId && l.OldHoldId == oldHoldId && l.NewHoldId != null && l.NewHold!.Generation > 0)
            .Select(l => l.NewHold!)
            .ToListAsync();
    }

    private static async Task<int> GenerationAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.CurrentGeneration).SingleAsync();
    }
}
