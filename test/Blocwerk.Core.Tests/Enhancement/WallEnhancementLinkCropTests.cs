// <copyright file="WallEnhancementLinkCropTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>
/// A link suggestion pairs holds on two panels. Its review must show both panels: before, it showed only the first
/// hold's photo, so a pair could not be judged and every suggestion was turned down. And an answered card whose hold
/// was merged away later still has its picture.
/// </summary>
public sealed class WallEnhancementLinkCropTests
{
    [Fact]
    public async Task LinkItem_HasAPictureOfEachHoldOnItsOwnPanelPhoto()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await f.SetPanelPhotoAsync(Solid(SKColors.Red));
        var otherPanel = await AddPanelAsync(f, Solid(SKColors.Blue));
        var left = await f.AddHoldAsync(0.5, 0.5);
        var right = await f.AddHoldAsync(0.5, 0.5, h => h.WallPanelId = otherPanel);
        var service = f.Service(FakeEnhancementStep.Suggesting("links", 600, WallEnhancementItemKind.Link, new WallEnhancementSuggestion(left, right, null, null)));
        await f.StartAndWaitAsync(service);
        var item = Assert.Single(await f.ItemsAsync());

        var mine = await service.CropAsync(f.WallId, item.Id);
        var theirs = await service.OtherCropAsync(f.WallId, item.Id);

        Assert.Equal(SKColors.Red, CentreOf(Assert.IsType<byte[]>(mine)));
        Assert.Equal(SKColors.Blue, CentreOf(Assert.IsType<byte[]>(theirs)));
    }

    [Fact]
    public async Task ItemWithoutAnOtherHold_HasNoSecondPicture()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await f.SetPanelPhotoAsync(Solid(SKColors.Red));
        var hold = await f.AddHoldAsync(0.5, 0.5);
        var service = f.Service(FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate, new WallEnhancementSuggestion(hold, null, null, null)));
        await f.StartAndWaitAsync(service);
        var item = Assert.Single(await f.ItemsAsync());

        Assert.NotNull(await service.CropAsync(f.WallId, item.Id));
        Assert.Null(await service.OtherCropAsync(f.WallId, item.Id));
    }

    [Fact]
    public async Task ItemWhoseHoldWasMergedAway_StillHasItsPicture()
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
            new WallEnhancementSuggestion(merged, other, null, nameof(HoldMergeMode.KeepLeft))));
        await f.StartAndWaitAsync(service);
        var items = await f.ItemsAsync();
        await service.DecideAsync(f.WallId, items.Single(i => i.HoldId == kept).Id, true);
        Assert.Null(await f.Harness.CreateContext().Holds.FindAsync(merged));

        var crop = await service.CropAsync(f.WallId, items.Single(i => i.HoldId == merged).Id);

        Assert.Equal(SKColors.Red, CentreOf(Assert.IsType<byte[]>(crop)));
    }

    private static async Task<Guid> AddPanelAsync(WallEnhancementFixture f, byte[] photo)
    {
        await using var db = f.Harness.CreateContext();
        var panel = new WallPanel { WallId = f.WallId, Col = 1, Row = 0, Photo = photo, PhotoContentType = "image/png", Generation = 0 };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static byte[] Solid(SKColor color)
    {
        using var bitmap = new SKBitmap(800, 600);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The crop's centre colour, snapped to pure red or blue (the JPEG is lossy and the outline is drawn over it).</summary>
    private static SKColor CentreOf(byte[] jpeg)
    {
        using var bitmap = SKBitmap.Decode(jpeg);
        var c = bitmap.GetPixel(bitmap.Width / 4, bitmap.Height / 2);
        return c.Red > c.Blue ? SKColors.Red : SKColors.Blue;
    }
}
