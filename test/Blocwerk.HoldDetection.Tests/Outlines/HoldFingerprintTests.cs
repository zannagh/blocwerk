using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>Fingerprint similarity on real crops, metric sizes, and JSON storage.</summary>
public class HoldFingerprintTests
{
    [Fact]
    public void SameHoldInTwoPhotos_BeatsDifferentHoldOfSimilarColour()
    {
        // The blue crescent from IMG_2770 (seen from below) and IMG_2783 (frontal) vs a different, rounder
        // blue hold in IMG_2783.
        HoldFingerprint a = HoldFixtures.Outline(HoldFixtures.Crescent2770).Result.Fingerprint;
        HoldFingerprint b = HoldFixtures.Outline(HoldFixtures.Crescent2783).Result.Fingerprint;
        HoldFingerprint other = HoldFixtures.Outline(HoldFixtures.ShadowBlue).Result.Fingerprint;

        double same = HoldFingerprint.Similarity(a, b);
        double diffA = HoldFingerprint.Similarity(a, other);
        double diffB = HoldFingerprint.Similarity(b, other);

        Assert.True(HoldFingerprintSimilarity.ColourScore(b, other) > 0.6, "the distractor must really be similar in colour");
        Assert.True(same > diffA + 0.15 && same > diffB + 0.15, $"same={same:F3} diffA={diffA:F3} diffB={diffB:F3}");
        Assert.True(same >= HoldRelocationMatcher.DefaultMinScore, $"same={same:F3}");
    }

    [Fact]
    public void Similarity_IsSymmetric_AndOneForIdentical()
    {
        HoldFingerprint a = HoldFixtures.Outline(HoldFixtures.Green).Result.Fingerprint;
        HoldFingerprint b = HoldFixtures.Outline(HoldFixtures.White).Result.Fingerprint;

        Assert.Equal(1.0, HoldFingerprint.Similarity(a, a), 6);
        Assert.Equal(HoldFingerprint.Similarity(a, b), HoldFingerprint.Similarity(b, a), 10);
        Assert.True(HoldFingerprint.Similarity(a, b) < 0.4, "green vs white must be clearly different");
    }

    [Fact]
    public void MetricSizes_AreUsedOnlyWhenBothHaveThem()
    {
        var hist = new double[HoldFingerprint.HueBins + 1];
        hist[4] = 1;
        var baseFp = new HoldFingerprint { L = 120, A = 100, B = 150, Aspect = 1.5, Solidity = 0.9, Histogram = hist };
        var big = baseFp with { WidthMm = 200, HeightMm = 130, AreaMm2 = 20000 };
        var small = baseFp with { WidthMm = 100, HeightMm = 65, AreaMm2 = 5000 };

        // Identical look, very different physical size → metric comparison separates them...
        Assert.True(HoldFingerprint.Similarity(big, small) < 0.8);
        Assert.Equal(1.0, HoldFingerprint.Similarity(big, big), 6);

        // ...while one side without millimetres falls back to the size-free score (pixel area ignored).
        var noMm = baseFp with { AreaPx = 123456 };
        Assert.Equal(1.0, HoldFingerprint.Similarity(big, noMm), 6);
        Assert.Null(HoldFingerprintSimilarity.SizeScore(big, noMm));
    }

    [Fact]
    public void Json_RoundTrips()
    {
        HoldFingerprint fp = HoldFixtures.Outline(HoldFixtures.Green).Result.Fingerprint with { WidthMm = 180.5 };

        string json = fp.ToJson();
        HoldFingerprint? back = HoldFingerprint.FromJson(json);

        Assert.NotNull(back);
        Assert.Contains("\"histogram\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("heightMm", json, StringComparison.Ordinal);
        Assert.Equal(fp.L, back!.L);
        Assert.Equal(fp.Histogram, back.Histogram);
        Assert.Equal(fp.Hu, back.Hu);
        Assert.Equal(180.5, back.WidthMm);
        Assert.Null(back.HeightMm);
        Assert.Equal(1.0, HoldFingerprint.Similarity(fp, back), 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void FromJson_ReturnsNullForGarbage(string? json)
    {
        Assert.Null(HoldFingerprint.FromJson(json));
    }

    [Fact]
    public void Histogram_IsNormalised()
    {
        HoldFingerprint fp = HoldFixtures.Outline(HoldFixtures.Crescent2783).Result.Fingerprint;

        Assert.Equal(HoldFingerprint.HueBins + 1, fp.Histogram.Length);
        Assert.Equal(1.0, fp.Histogram.Sum(), 2);
        Assert.Equal(7, fp.Hu.Length);
        Assert.True(fp.Aspect > 1.8, "a crescent is elongated");
    }
}
