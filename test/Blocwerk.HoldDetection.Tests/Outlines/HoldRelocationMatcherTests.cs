using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Position-free "same hold, moved" proposals: thresholds, 1:1 assignment and the ambiguity margin.</summary>
public class HoldRelocationMatcherTests
{
    private static readonly HoldFingerprint Red = Fp(100, 190, 160, aspect: 1.2);
    private static readonly HoldFingerprint Blue = Fp(80, 130, 90, aspect: 2.2);
    private static readonly HoldFingerprint Yellow = Fp(200, 115, 190, aspect: 1.0);

    [Fact]
    public void DistinctHolds_AreEachMatchedToTheirMovedCounterpart()
    {
        var olds = new[] { C(Red), C(Blue), C(Yellow) };
        var news = new[] { C(Yellow with { L = 205 }), C(Red with { A = 188 }), C(Blue with { Aspect = 2.1 }) };

        var proposals = new HoldRelocationMatcher().Propose(olds, news);

        Assert.Equal(3, proposals.Count);
        Assert.Contains(proposals, p => p.DisappearedHoldId == olds[0].HoldId && p.AppearedHoldId == news[1].HoldId);
        Assert.Contains(proposals, p => p.DisappearedHoldId == olds[1].HoldId && p.AppearedHoldId == news[2].HoldId);
        Assert.Contains(proposals, p => p.DisappearedHoldId == olds[2].HoldId && p.AppearedHoldId == news[0].HoldId);
        Assert.All(proposals, p => Assert.True(p.Score >= HoldRelocationMatcher.DefaultMinScore));
        Assert.Equal(proposals.OrderByDescending(p => p.Score).Select(p => p.Score), proposals.Select(p => p.Score));
    }

    [Fact]
    public void TwoLookAlikeCandidates_AreAmbiguous_AndNotProposed()
    {
        // One red hold vanished; two near-identical red holds appeared. Either could be it → propose nothing.
        var olds = new[] { C(Red) };
        var news = new[] { C(Red with { L = 101 }), C(Red with { L = 99 }), C(Blue) };

        var proposals = new HoldRelocationMatcher().Propose(olds, news);

        Assert.Empty(proposals);
    }

    [Fact]
    public void AmbiguityMargin_IsConfigurable()
    {
        var olds = new[] { C(Red) };
        var news = new[] { C(Red), C(Red with { Aspect = 1.4 }) };

        var strict = new HoldRelocationMatcher(minMargin: 0.2).Propose(olds, news);
        var lenient = new HoldRelocationMatcher(minMargin: 0.0).Propose(olds, news);

        Assert.Empty(strict);
        var only = Assert.Single(lenient);
        Assert.Equal(news[0].HoldId, only.AppearedHoldId);
        Assert.True(only.Margin > 0);
    }

    [Fact]
    public void TwoVanishedLookAlikes_CompetingForOneNewHold_AreAmbiguous()
    {
        var olds = new[] { C(Blue), C(Blue with { B = 91 }) };
        var news = new[] { C(Blue) };

        Assert.Empty(new HoldRelocationMatcher().Propose(olds, news));
    }

    [Fact]
    public void BelowMinScore_IsNotProposed_AndEmptyInputsAreFine()
    {
        var matcher = new HoldRelocationMatcher();

        Assert.Empty(matcher.Propose([C(Red)], [C(Yellow)]));
        Assert.Empty(matcher.Propose([], [C(Yellow)]));
        Assert.Empty(matcher.Propose([C(Red)], []));
    }

    [Fact]
    public void Assignment_IsOneToOne()
    {
        // Both old reds are closest to the same new red; only one may take it, and only if unambiguous.
        var olds = new[] { C(Red), C(Yellow) };
        var news = new[] { C(Red), C(Yellow), C(Yellow with { A = 150 }) };

        var proposals = new HoldRelocationMatcher().Propose(olds, news);

        Assert.Equal(proposals.Count, proposals.Select(p => p.AppearedHoldId).Distinct().Count());
        Assert.Equal(proposals.Count, proposals.Select(p => p.DisappearedHoldId).Distinct().Count());
        Assert.Contains(proposals, p => p.DisappearedHoldId == olds[0].HoldId && p.AppearedHoldId == news[0].HoldId);
    }

    private static RelocationCandidate C(HoldFingerprint fp) => new(Guid.NewGuid(), fp);

    private static HoldFingerprint Fp(double l, double a, double b, double aspect)
    {
        // A histogram consistent with the dominant colour: all mass in its hue bin.
        var hist = new double[HoldFingerprint.HueBins + 1];
        double hue = (Math.Atan2(b - 128, a - 128) * 180 / Math.PI) + 360;
        hist[(int)(hue % 360 / 30)] = 1;
        return new HoldFingerprint
        {
            L = l,
            A = a,
            B = b,
            Aspect = aspect,
            Solidity = 0.9,
            Histogram = hist,
            Hu = [0.75, 2.3 + aspect, 3.5, 4.5, 8.5, 5.8, 9.0],
        };
    }
}
