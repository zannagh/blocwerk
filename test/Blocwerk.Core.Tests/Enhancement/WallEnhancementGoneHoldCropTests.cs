// <copyright file="WallEnhancementGoneHoldCropTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>
/// A review picture of a hold that is gone since the run is drawn from its journalled delete: only from this wall's
/// batches since the run started, and never as another wall's hold.
/// </summary>
public sealed class WallEnhancementGoneHoldCropTests
{
    [Fact]
    public async Task OtherCrop_OfAPartnerMergedAway_IsStillDrawn()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.Duplicates = new HoldDuplicateService(
            f.Journalling, f.Harness.CurrentUser, f.Journal, new ChangeJournalReverter(f.Journalling, f.Journal), NullLogger<HoldDuplicateService>.Instance);
        await f.SetPanelPhotoAsync(Solid(SKColors.Red));
        var kept = await f.AddHoldAsync(0.5, 0.5);
        var merged = await f.AddHoldAsync(0.52, 0.5);
        var other = await f.AddHoldAsync(0.2, 0.2);
        var service = f.Service(FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate,
            new WallEnhancementSuggestion(kept, merged, null, nameof(HoldMergeMode.KeepLeft)),
            new WallEnhancementSuggestion(other, merged, null, nameof(HoldMergeMode.KeepLeft))));
        await f.StartAndWaitAsync(service);
        var items = await f.ItemsAsync();
        await service.DecideAsync(f.WallId, items.Single(i => i.HoldId == kept).Id, true);

        var crop = await service.OtherCropAsync(f.WallId, items.Single(i => i.HoldId == other).Id);

        Assert.NotNull(crop);
    }

    [Fact]
    public async Task ADeleteRecordedBeforeTheRun_IsNotRead()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var (service, item, hold) = await GoneHoldItemAsync(f);
        var run = await f.RunAsync();
        await RecordDeleteAsync(f, hold, f.WallId, f.WallId, run.CreatedAt.AddMinutes(-5));

        Assert.Null(await service.CropAsync(f.WallId, item));

        await RecordDeleteAsync(f, hold, f.WallId, f.WallId, run.CreatedAt.AddMinutes(1));
        Assert.NotNull(await service.CropAsync(f.WallId, item));
    }

    [Fact]
    public async Task AnotherWallsDelete_IsNotRead()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var (service, item, hold) = await GoneHoldItemAsync(f);
        var run = await f.RunAsync();
        var otherWall = Guid.NewGuid();

        await RecordDeleteAsync(f, hold, otherWall, f.WallId, run.CreatedAt.AddMinutes(1));
        Assert.Null(await service.CropAsync(f.WallId, item));

        await RecordDeleteAsync(f, hold, f.WallId, otherWall, run.CreatedAt.AddMinutes(2));
        Assert.Null(await service.CropAsync(f.WallId, item));
    }

    /// <summary>A run with one item for a hold that is then removed without a journal entry.</summary>
    private static async Task<(WallEnhancementService Service, Guid ItemId, Hold Hold)> GoneHoldItemAsync(WallEnhancementFixture f)
    {
        await f.SetPanelPhotoAsync(Solid(SKColors.Red));
        var id = await f.AddHoldAsync(0.5, 0.5);
        var service = f.Service(FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate,
            new WallEnhancementSuggestion(id, null, null, null)));
        await f.StartAndWaitAsync(service);
        var hold = await f.GetHoldAsync(id);
        await using var db = f.Harness.CreateContext();
        await db.Holds.Where(h => h.Id == id).ExecuteDeleteAsync();
        return (service, Assert.Single(await f.ItemsAsync()).Id, hold);
    }

    /// <summary>A journalled delete of <paramref name="hold"/>, in a batch scoped to <paramref name="scopeWall"/>, recording it on <paramref name="rowWall"/>.</summary>
    private static async Task RecordDeleteAsync(WallEnhancementFixture f, Hold hold, Guid scopeWall, Guid rowWall, DateTimeOffset at)
    {
        await using var db = f.Harness.CreateContext();
        var batch = new ChangeJournalBatch { Label = "test-delete", ScopeKind = ChangeJournalScopeKind.Wall, ScopeId = scopeWall, CreatedAt = at };
        db.ChangeJournalBatches.Add(batch);
        var before = new { hold.Id, WallId = rowWall, hold.WallPanelId, hold.X, hold.Y, hold.Radius, hold.Generation };
        db.ChangeJournalEntries.Add(new ChangeJournalEntry
        {
            BatchId = batch.Id,
            EntityType = nameof(Hold),
            KeyJson = JsonSerializer.Serialize(new { hold.Id }),
            Op = ChangeJournalOp.Delete,
            BeforeJson = JsonSerializer.Serialize(before),
            CreatedAt = at,
        });
        await db.SaveChangesAsync();
    }

    private static byte[] Solid(SKColor color)
    {
        using var bitmap = new SKBitmap(800, 600);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
