// <copyright file="WallRefreshRecheckTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Review now or wait for 3D": the update can be applied while this visit's 3D model is still building; once the model
/// is active with textures, the quick review is worked out again with it (Apply waits for that), unless the user already
/// changed the decisions (before or during the check), which are kept. A failed check keeps the prepared update. Without a
/// texture matcher the check runs without 3D and says nothing about it (invisible fallback).
/// </summary>
public class WallRefreshRecheckTests
{
    [Fact]
    public async Task WhileTheModelBuilds_TheUpdateCanBeApplied_AndNoCheckIsPending()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();

        var view = await s.PrepareAsync();

        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        Assert.False(view.Check3DPending);
        Assert.Null(view.Summary!.CheckedWithModelId);
        Assert.NotNull(view.Summary.DecisionsRecordedAt);
        await s.Service.ApplyAsync(view.Id);
    }

    [Fact]
    public async Task WhenTheModelIsReady_TheReviewIsCheckedAgain_BeforeItCanBeApplied()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var prepared = await s.PrepareAsync();
        var model = await MakeModelReadyAsync(h, prepared.CaptureId!.Value);

        var pending = await s.CurrentAsync();
        Assert.True(pending.Check3DPending);
        await Assert.ThrowsAsync<UserFacingException>(() => s.Service.ApplyAsync(prepared.Id));

        await s.RunQueuedAsync();

        var checkedView = await s.CurrentAsync();
        Assert.False(checkedView.Check3DPending);
        Assert.Equal(model, checkedView.Summary!.Attempted3DModelId);
        Assert.Null(checkedView.Summary.CheckedWithModelId);
        Assert.NotEqual(prepared.Summary!.DecisionsRecordedAt, checkedView.Summary.DecisionsRecordedAt);
        Assert.Equal(prepared.Summary.NewHolds, checkedView.Summary.NewHolds);
        Assert.Equal("Check the summary, then apply", checkedView.Steps.Single(st => st.Key == RefreshTimeline.Review).Detail);
        await s.Service.ApplyAsync(prepared.Id);
    }

    [Fact]
    public async Task WhenTheUserChangedTheDecisions_TheyAreKept()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var prepared = await s.PrepareAsync();
        await using (var db = h.CreateContext())
        {
            var session = await db.WallUpdateSessions.SingleAsync(x => x.Id == prepared.UpdateSessionId);
            session.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        var model = await MakeModelReadyAsync(h, prepared.CaptureId!.Value);
        _ = await s.CurrentAsync();
        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.Equal(model, view.Summary!.Attempted3DModelId);
        Assert.Equal(prepared.Summary!.DecisionsRecordedAt, view.Summary.DecisionsRecordedAt);
        Assert.Contains("your own choices", view.Steps.Single(st => st.Key == RefreshTimeline.Review).Detail);
        await s.Service.ApplyAsync(prepared.Id);
    }

    [Fact]
    public async Task AFailedCheck_KeepsThePreparedUpdate()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var prepared = await s.PrepareAsync();
        s.Decorate = actors => actors with { BigUpdate = new HookedBigUpdate(actors.BigUpdate, () => throw new InvalidOperationException("matcher crashed")) };
        var model = await MakeModelReadyAsync(h, prepared.CaptureId!.Value);
        _ = await s.CurrentAsync();

        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        Assert.False(view.Check3DPending);
        Assert.Equal(model, view.Summary!.Attempted3DModelId);
        Assert.Null(view.Summary.CheckedWithModelId);
        Assert.Contains("did not work", view.Steps.Single(st => st.Key == RefreshTimeline.Review).Detail);
        Assert.Equal(prepared.UpdateSessionId, (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))?.Id);
        await s.Service.ApplyAsync(prepared.Id);
    }

    [Fact]
    public async Task AChoiceMadeDuringTheCheck_IsKept()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var prepared = await s.PrepareAsync();
        s.Decorate = actors => actors with { BigUpdate = new HookedBigUpdate(actors.BigUpdate, () => TouchSessionAsync(h, prepared.UpdateSessionId!.Value)) };
        var model = await MakeModelReadyAsync(h, prepared.CaptureId!.Value);
        _ = await s.CurrentAsync();

        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.Equal(model, view.Summary!.Attempted3DModelId);
        Assert.Equal(prepared.Summary!.DecisionsRecordedAt, view.Summary.DecisionsRecordedAt);
        Assert.Contains("your own choices", view.Steps.Single(st => st.Key == RefreshTimeline.Review).Detail);
    }

    [Fact]
    public void AnInterruptedCheck_DoesNotBlockApplyForever()
    {
        var refresh = new WallRefresh { Status = WallRefreshStatus.ReadyToApply };
        RefreshTimeline.Set(refresh, RefreshTimeline.Review, RefreshStepState.Running, "Checking");

        Assert.True(WallRefreshProcessor.IsRechecking(refresh, refresh.UpdatedAt.AddMinutes(1)));
        Assert.False(WallRefreshProcessor.IsRechecking(refresh, refresh.UpdatedAt + WallRefreshProcessor.RecheckStale));
    }

    private static async Task TouchSessionAsync(WallTestHarness h, Guid sessionId)
    {
        await using var db = h.CreateContext();
        var session = await db.WallUpdateSessions.SingleAsync(x => x.Id == sessionId);
        session.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await db.SaveChangesAsync();
    }

    internal static async Task<Guid> MakeModelReadyAsync(WallTestHarness h, Guid captureId)
    {
        await using var db = h.CreateContext();
        var model = new WallGeometryModel { WallId = h.WallId, Json = "{}", Source = "test", IsActive = true };
        db.WallGeometryModels.Add(model);
        db.WallGeometryTextures.Add(new WallGeometryTexture
        {
            GeometryModelId = model.Id, FacetId = "0", StoredPath = "t.jpg", AMax = 1000, BMax = 1000, WidthPx = 100, HeightPx = 100,
        });
        var capture = await db.WallCaptures.SingleAsync(c => c.Id == captureId);
        capture.GeometryModelId = model.Id;
        await db.SaveChangesAsync();
        return model.Id;
    }
}
