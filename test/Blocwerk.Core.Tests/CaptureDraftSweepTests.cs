// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Drafts are swept after a day without uploads (not a day after they were created), never while an open "Update panels
/// + 3D" run owns them, and the startup sweep takes a draft's video with it like the periodic one.
/// </summary>
public class CaptureDraftSweepTests
{
    [Fact]
    public async Task AnOldDraft_WithARecentUpload_IsKept()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await DraftAsync(h, s, created: TimeSpan.FromDays(3), uploaded: TimeSpan.FromHours(1));

        Assert.Equal(0, await CaptureScenario.Sweeper(s).SweepDraftsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        await using var db = h.CreateContext();
        Assert.True(await db.WallCaptures.AnyAsync(c => c.Id == draftId));
    }

    [Theory]
    [InlineData(WallRefreshStatus.Uploading, false)]
    [InlineData(WallRefreshStatus.ReadyToStart, false)]
    [InlineData(WallRefreshStatus.Discarded, true)]
    public async Task AnIdleDraft_IsKeptWhileAnOpenUpdateRunOwnsIt(WallRefreshStatus status, bool swept)
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await DraftAsync(h, s, created: TimeSpan.FromDays(2), uploaded: TimeSpan.FromDays(2));
        await using (var db = h.CreateContext())
        {
            db.WallRefreshes.Add(new WallRefresh { WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = status, CaptureId = draftId });
            await db.SaveChangesAsync();
        }

        Assert.Equal(swept ? 1 : 0, await CaptureScenario.Sweeper(s).SweepDraftsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task TheStartupSweep_DeletesTheDraftsVideoToo()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var draftId = await DraftAsync(h, s, created: TimeSpan.FromDays(2), uploaded: TimeSpan.FromDays(2));
        var video = await s.Files.SaveAsync(new byte[] { 1, 2, 3 }, ".mp4", CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            (await db.WallCaptures.SingleAsync(c => c.Id == draftId)).VideoStoredPath = video;
            await db.SaveChangesAsync();
        }

        await new WallCaptureWorker(h.RootContextFactory, s.Queue, s.Processor, CaptureScenario.Sweeper(s), NullLogger<WallCaptureWorker>.Instance)
            .RecoverAsync(CancellationToken.None);

        Assert.False(File.Exists(s.Files.ResolvePhysicalPath(video)));
        await using var read = h.CreateContext();
        Assert.False(await read.WallCaptures.AnyAsync(c => c.Id == draftId));
    }

    /// <summary>A draft with one photo, created and last uploaded the given time ago.</summary>
    private static async Task<Guid> DraftAsync(WallTestHarness h, CaptureScenario s, TimeSpan created, TimeSpan uploaded)
    {
        await h.SeedWallAsync(holdCount: 0);
        await WallGlyphSettingsTests.Service(h).SetGlyphSettingsAsync(h.WallId, true, 125);
        var draft = await s.Service.CreateDraftAsync(h.WallId);
        await s.Service.AddPhotoAsync(draft.CaptureId, "a.jpg", CaptureScenario.TinyJpeg(), CancellationToken.None);
        await using var db = h.CreateContext();
        (await db.WallCaptures.SingleAsync(c => c.Id == draft.CaptureId)).CreatedAt = DateTimeOffset.UtcNow - created;
        (await db.WallCapturePhotos.SingleAsync(p => p.CaptureId == draft.CaptureId)).UploadedAt = DateTimeOffset.UtcNow - uploaded;
        await db.SaveChangesAsync();
        return draft.CaptureId;
    }
}
