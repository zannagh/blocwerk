// <copyright file="FlushedDeletionNoticeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.State;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// W4: a flushed wall batch saves its deletions, and the notice after the reload names every boulder they retired,
/// linking to each. The names come from the delete itself (HoldDeletion), not from a guess beforehand.
/// </summary>
public class FlushedDeletionNoticeTests
{
    [Fact]
    public async Task AFlushedDeletionProducesANoticeNamingItsBoulders()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync();
        var crimp = await harness.BoulderService.CreateBoulderAsync(harness.WallId, "Crimp Line", null, [new BoulderHoldInput(holds[0].Id)]);
        var sloper = await harness.BoulderService.CreateBoulderAsync(harness.WallId, "Sloper", null, [new BoulderHoldInput(holds[0].Id), new BoulderHoldInput(holds[1].Id)]);
        await harness.BoulderService.CreateBoulderAsync(harness.WallId, "Elsewhere", null, [new BoulderHoldInput(holds[1].Id)]);

        // What WallDetail's batch save does per deleted hold.
        var retiredBySave = new List<RetiredBoulder>();
        retiredBySave.AddRange(await harness.WallService.DeleteHoldRetiringAsync(holds[0].Id));

        var flaggedBeforeSave = new List<AffectedBoulder> { new(sloper.Id, "Sloper"), new(Guid.NewGuid(), "Marked Only") };
        var (retired, flagged) = WallFlushNotice.From(retiredBySave, flaggedBeforeSave);
        var text = WallFlushNotice.Text(retired, flagged);
        var links = WallFlushNotice.Links(harness.WallId, retired, flagged);

        Assert.Equal(["Crimp Line", "Sloper"], retired.Select(b => b.Name));
        Assert.Equal(["Marked Only"], flagged.Select(b => b.Name));
        Assert.Contains("2 boulder(s) were retired", text);
        Assert.Contains("1 flagged for review", text);
        Assert.Contains(new NoticeLink("Crimp Line (retired)", $"/walls/{harness.WallId}/boulders/{crimp.Id}"), links);
        Assert.Contains(new NoticeLink("Sloper (retired)", $"/walls/{harness.WallId}/boulders/{sloper.Id}"), links);
        Assert.DoesNotContain(links, l => l.Text.StartsWith("Elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public void AFlushWithoutDeletionsOrMarksSaysOnlySaved()
    {
        Assert.Equal("Your unsaved hold edits were saved before an update.", WallFlushNotice.Text([], []));
        Assert.Empty(WallFlushNotice.Links(Guid.NewGuid(), [], []));
    }
}
