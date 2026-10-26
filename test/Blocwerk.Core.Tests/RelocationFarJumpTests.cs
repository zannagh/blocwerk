// <copyright file="RelocationFarJumpTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hold re-set across the wall (The Attic, end of October: the orange hold jumped about 1100 px). The suggestion is made
/// however far the jump is, and only a person's "it moved here" acts on it: the hold moves with its boulders, and the
/// detection at the new spot does not become a second, duplicate hold.
/// </summary>
public class RelocationFarJumpTests
{
    [Fact]
    public async Task AJumpAcrossTheWall_IsSuggested_AndConfirmingIt_MovesTheHold_WithoutADuplicate()
    {
        using var h = new WallTestHarness();

        // A real 4032 × 3024 photo and a matcher that predicts M where it was: M' is about 1450 px from that spot, far beyond
        // the 161 px (4 % of the long side) a jump used to be capped at.
        var s = await RelocationScenario.SeedAsync(h, stagedPhoto: Photo(4032, 3024));
        await s.BigUpdate(identityWarp: true).ResumeAsync(s.WallId);

        var suggestion = Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));
        Assert.Equal((s.OldM, s.NewM), (suggestion.OldHoldId, suggestion.NewHoldId));

        await s.Sessions().DecideRelocationAsync(s.WallId, suggestion.Id, RelocationDecision.Moved);
        await s.ContinueAsync();
        await s.PromoteAsync(identityWarp: true);

        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM);
        Assert.Equal((s.NewM, HoldGenerationLinkKind.Changed), (link.NewHoldId, link.Kind));

        // One hold for M, at its new spot: no copy left at the old spot, no second hold made of the detection.
        var live = await db.Holds.Where(x => x.Generation == 3).ToListAsync();
        Assert.Equal(3, live.Count);
        Assert.DoesNotContain(live, x => Math.Abs(x.X - 0.5) < 0.01 && Math.Abs(x.Y - 0.5) < 0.01);
        Assert.Equal((0.8, 0.3, "Green blob"), live.Where(x => x.Id == s.NewM).Select(x => (x.X, x.Y, x.Name)).Single());

        var route = await db.BoulderHolds.Where(bh => bh.BoulderId == s.BoulderId).Select(bh => bh.HoldId).ToListAsync();
        Assert.Contains(s.NewM, route);
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
    }

    [Fact]
    public async Task AJumpAcrossTheWall_LeftUnanswered_ChangesNothing()
    {
        using var h = new WallTestHarness();
        var s = await RelocationScenario.SeedAsync(h, stagedPhoto: Photo(4032, 3024));
        await s.BigUpdate(identityWarp: true).ResumeAsync(s.WallId);
        Assert.Single(await s.Sessions().GetRelocationSuggestionsAsync(s.WallId));

        await s.ContinueAsync();
        await s.PromoteAsync(identityWarp: true);

        await using var db = h.CreateContext();
        Assert.Equal(HoldGenerationLinkKind.Same, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldM)).Kind);
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    private static byte[] Photo(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(90, 90, 90));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
