// <copyright file="LookOverListingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.Components.Shared.LookOver;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The page at 159 findings: it opens on the findings whose hold a boulder uses, keeps every card where it was while it
/// is open, answers a kind at once in two scopes, and says in its labels only what is true of the finding at hand.
/// </summary>
public class LookOverListingTests
{
    private static readonly LookOverBoulder Crimp = new(Guid.NewGuid(), "Crimp line", true);
    private static readonly LookOverBoulder Warmup = new(Guid.NewGuid(), "Warm-up", false);

    [Fact]
    public void AnAnsweredCard_KeepsItsPlace_AndItsView_EvenWhenItsBouldersAreGone()
    {
        var (first, second, third) = (View(LookOverFindingKind.NotFound, Crimp), View(LookOverFindingKind.NotFound, Warmup), View(LookOverFindingKind.NotFound));
        var listing = new LookOverListing();
        listing.Take([first, second, third]);

        // Answered "Gone": the service now sorts it last and its boulders are historic. The page does not move it.
        var answered = first with { Status = LookOverFindingStatus.Settled, Answer = LookOverAnswer.RemoveHold, Boulders = [], HoldId = null };
        listing.Take([second, third, answered]);

        Assert.Equal([first.Id, second.Id, third.Id], listing.Findings.Select(f => f.Id));
        Assert.Equal([first.Id, second.Id], listing.Visible(LookOverFindingKind.NotFound, onBouldersOnly: true, boulderId: null).Select(f => f.Id));
        Assert.Equal([first.Id], listing.Visible(LookOverFindingKind.NotFound, onBouldersOnly: true, boulderId: Crimp.Id).Select(f => f.Id));
        Assert.Equal((2, 3, 2), listing.Counts(LookOverFindingKind.NotFound));
    }

    [Fact]
    public void TheDefaultView_ShowsOnlyFindingsOnBoulders_AndTheKindsThatHaveAny()
    {
        var listing = new LookOverListing();
        listing.Take([View(LookOverFindingKind.SameSpot), View(LookOverFindingKind.UnsureMatch, Crimp), View(LookOverFindingKind.UnsureMatch), View(LookOverFindingKind.ReplacedByOther, Warmup)]);

        Assert.Equal([LookOverFindingKind.ReplacedByOther, LookOverFindingKind.UnsureMatch], listing.Kinds(onBouldersOnly: true, boulderId: null));
        Assert.Equal(
            [LookOverFindingKind.ReplacedByOther, LookOverFindingKind.UnsureMatch, LookOverFindingKind.SameSpot],
            listing.Kinds(onBouldersOnly: false, boulderId: null));
        Assert.Equal([LookOverFindingKind.UnsureMatch], listing.Kinds(onBouldersOnly: false, boulderId: Crimp.Id));
        Assert.Equal((1, 2, 2), listing.Counts(LookOverFindingKind.UnsureMatch));
        Assert.Equal("1 on boulders · 2 in all", LookOverCopy.GroupCounts(1, 2));
        Assert.Equal("Crimp line", listing.BoulderName(Crimp.Id));
        Assert.Null(listing.BoulderName(Guid.NewGuid()));
    }

    [Fact]
    public void TheBulkAnswer_CountsItsTwoScopes_FromWhatIsOpenNow()
    {
        var listing = new LookOverListing();
        var done = View(LookOverFindingKind.UnsureMatch) with { Status = LookOverFindingStatus.Settled };
        listing.Take([View(LookOverFindingKind.UnsureMatch, Crimp), View(LookOverFindingKind.UnsureMatch), View(LookOverFindingKind.UnsureMatch), done]);

        Assert.Equal((3, 2), listing.BulkScopes(LookOverFindingKind.UnsureMatch));
        Assert.Equal("Mark the 2 unsure matches that no boulder uses as same hold", LookOverCopy.BulkLabel(LookOverFindingKind.UnsureMatch, 2, withoutBoulders: true));
        Assert.Equal("Mark all 3 unsure matches as same hold", LookOverCopy.BulkLabel(LookOverFindingKind.UnsureMatch, 3, withoutBoulders: false));
        Assert.Equal("Keep both everywhere (24)", LookOverCopy.BulkLabel(LookOverFindingKind.SameSpot, 24, withoutBoulders: false));
    }

    [Fact]
    public async Task SettleAll_TakesTheScope_AndOnlyTheKindsThatCanBeAnsweredAtOnce()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var unused = await AddFindingAsync(h, new() { WallId = w.WallId, Generation = 3, Kind = LookOverFindingKind.UnsureMatch });
        var sameSpot = await AddFindingAsync(h, new() { WallId = w.WallId, Generation = 3, Kind = LookOverFindingKind.SameSpot });
        var service = Service(h);

        Assert.Equal(1, await service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, onlyWithoutBoulders: true));

        var after = (await service.ListAsync(w.WallId)).ToDictionary(f => f.Id, f => f.Status);
        Assert.Equal(LookOverFindingStatus.Settled, after[unused]);
        Assert.Equal(2, after.Values.Count(s => s == LookOverFindingStatus.Open));
        Assert.Equal(1, await service.SettleAllAsync(w.WallId, LookOverFindingKind.SameSpot, onlyWithoutBoulders: false));
        Assert.Equal(1, await service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, onlyWithoutBoulders: false));
        Assert.Equal(LookOverFindingStatus.Settled, (await service.ListAsync(w.WallId)).Single(f => f.Id == sameSpot).Status);
        await Assert.ThrowsAsync<UserFacingException>(() => service.SettleAllAsync(w.WallId, LookOverFindingKind.NotFound, onlyWithoutBoulders: false));
    }

    [Fact]
    public async Task TheServicesOrder_PutsFindingsOnBouldersFirst_AndTheBannerCountsBouldersAsTheyAre()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        await AddFindingAsync(h, new() { WallId = w.WallId, Generation = 3, Kind = LookOverFindingKind.UnsureMatch, Confidence = 0.1 });
        var service = Service(h);

        var listed = await service.ListAsync(w.WallId);
        var state = (await service.GetStateAsync(w.WallId))!;

        Assert.Equal([1, 0], listed.Select(f => f.Boulders.Count));

        // An unsure match marks the hold, not the boulder: no boulder is marked, one uses a hold in question.
        Assert.Equal((0, 1), (state.BouldersMarked, state.MoreBoulders));
        Assert.Contains("0 boulders are marked for review; 1 more uses a hold the update was unsure about", LookOverCopy.BannerHint(state));
    }

    [Fact]
    public void TheMerge_IsNamedForTheBoulders_OnlyWhenThereAreAny()
    {
        var used = View(LookOverFindingKind.ReplacedByOther, Crimp, Warmup);
        var unused = View(LookOverFindingKind.SameSpot);

        Assert.Equal("Boulders should use the new hold", LookOverCopy.MergeLabel(used));
        Assert.Contains("2 boulders (Crimp line, Warm-up) keep the same mark", LookOverCopy.UseNewQuestion(used));
        Assert.Equal("Merge into the new hold", LookOverCopy.MergeLabel(unused));
        Assert.DoesNotContain("boulder", LookOverCopy.UseNewQuestion(unused), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheCards_ClaimOnlyWhatTheUpdateDid_AndSayWhatAnAnswerDid()
    {
        var notFound = View(LookOverFindingKind.NotFound, Crimp) with { HoldName = "white" };

        Assert.Equal("The update did not find the white hold again on the new photo. It was kept where it was.", LookOverCopy.Detail(notFound));
        Assert.All(Enum.GetValues<LookOverFindingKind>(), k => Assert.DoesNotContain("no longer looks", LookOverCopy.Detail(View(k, Crimp))));
        var removed = notFound with { Status = LookOverFindingStatus.Settled, Answer = LookOverAnswer.RemoveHold, AnswerBoulders = 3, AnsweredBy = "Pat", HoldId = null };
        Assert.Equal(("Hold removed · Pat", "Hold removed; 3 boulders are now historic."), (LookOverCopy.AnsweredLabel(removed), LookOverCopy.ResultLine(removed)));
        var still = notFound with { Status = LookOverFindingStatus.Settled, Answer = LookOverAnswer.Settle, ClearedBoulders = 1 };
        Assert.Equal(("Still there", "1 boulder is no longer marked for review."), (LookOverCopy.AnsweredLabel(still), LookOverCopy.ResultLine(still)));
        Assert.Contains("Undo under Wall settings", LookOverCopy.ResultLine(notFound with { Answer = LookOverAnswer.UseNewHold }));
    }

    [Fact]
    public void TheHeader_SaysOnceWhereTheFindingsComeFrom()
    {
        LookOverFindingView[] findings =
        [
            View(LookOverFindingKind.UnsureMatch), View(LookOverFindingKind.ReplacedByOther), View(LookOverFindingKind.NotFound),
            View(LookOverFindingKind.SameSpot), View(LookOverFindingKind.SameSpot),
        ];

        Assert.Equal(
            "5 findings: 2 from the update's review cards, 1 hold on a boulder not found again, 2 spots where an old and a new hold now sit together.",
            LookOverCopy.Breakdown(findings));
        Assert.Null(LookOverCopy.Breakdown([]));
    }

    [Theory]
    [InlineData("Yellow crimp", "yellow", "Yellow crimp")]
    [InlineData(null, "white", "The white hold")]
    [InlineData(null, null, "A hold")]
    public void TheBoulderNotice_NamesTheHold_ByNameElseColour(string? name, string? colour, string expected)
    {
        var move = new BoulderMoveView(null, null, name, 0, HoldMoveSource.TwoD, HoldMoveOutcome.NotFound, "not found again", HoldType.Normal, colour);

        Assert.Equal(expected, BoulderMovesNotice.HoldLabel(move));
    }

    [Fact]
    public void ATexturePictureThatIsMostlyBlack_IsNoPicture()
    {
        using var black = new SkiaSharp.SKBitmap(64, 64);
        black.Erase(SkiaSharp.SKColors.Black);
        using var image = SkiaSharp.SKImage.FromBitmap(black);
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);

        Assert.True(LookOverCrops.MostlyEmpty(data.ToArray()));
        Assert.False(LookOverCrops.MostlyEmpty(CaptureScenario.TinyJpeg(1)));
        Assert.True(LookOverCrops.MostlyEmpty([1, 2, 3]));
    }

    private static LookOverFindingView View(LookOverFindingKind kind, params LookOverBoulder[] boulders) =>
        new(Guid.NewGuid(), kind, LookOverFindingStatus.Open, null, 3, Guid.NewGuid(), Guid.NewGuid(), null, null, true, false, null, null, false, boulders, []);
}
