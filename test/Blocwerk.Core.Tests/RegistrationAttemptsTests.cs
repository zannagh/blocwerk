using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A weak photo × facet registration is retried with deterministic attempts and seeded from anchors, the best kept;
/// a strong one and a facet out of view are not retried.
/// </summary>
public class RegistrationAttemptsTests
{
    private static readonly TexturePlaneFrame Frame = new("0", -100, 2100, -100, 3100, 2200, 3200);

    [Fact]
    public void Retries_AreFixedPerPhotoAndFacet_AndCoverEachCoarseScale()
    {
        var a = RegistrationAttempts.Retries("c1 r0", "0").ToList();

        Assert.Equal(a, RegistrationAttempts.Retries("c1 r0", "0"));
        Assert.Equal(RegistrationAttempts.Seeds * 2, a.Count);
        Assert.All(a.Take(2), x => Assert.True(x.RansacSeed == 0 && x.ScaleJitter == 1));
        Assert.Equal([0, 1], a.Select(x => x.CoarsePass ?? -1).Distinct().Order());
        Assert.DoesNotContain(0, a.Skip(2).Select(x => x.RansacSeed));
        Assert.Empty(RegistrationAttempts.Retries("c1 r0", "5").Skip(2).Select(x => x.RansacSeed).Intersect(a.Skip(2).Select(x => x.RansacSeed)));
    }

    [Fact]
    public void AWeakMatch_IsRetried_AndTheStrongerAttemptKept()
    {
        var matcher = new FakePhotoTextureMatcher();
        matcher.Views.Add(new FakeTextureView(10, 0, 0, 2000, 200, HoldPlacementScenario.Shift(99.5)));
        matcher.Views.Add(new FakeTextureView(10, 0, 0, 2000, 80, HoldPlacementScenario.Shift(99.5), OnlyOnRetry: true));

        var r = Register(matcher).Single();

        Assert.True(RegistrationAttempts.IsStrong(r));
        Assert.Single(matcher.Retries); // the first strong attempt ends the retries
    }

    [Fact]
    public void AWeakMatch_ThatNoAttemptImproves_KeepsTheFirstResult_WithinTheBudget()
    {
        var matcher = new FakePhotoTextureMatcher();
        matcher.Views.Add(new FakeTextureView(10, 0, 0, 2000, 200, HoldPlacementScenario.Shift(99.5)));

        var r = Register(matcher).Single();

        Assert.True(r.Accepted);
        Assert.True(r.Inliers < RegistrationAttempts.StrongInliers);
        Assert.Equal(RegistrationAttempts.Seeds * 2, matcher.Retries.Count);
        var once = new FakePhotoTextureMatcher { Views = { matcher.Views[0] } };
        Assert.Equal(r.Inliers, Register(once, TimeSpan.Zero).Single().Inliers);
        Assert.Empty(once.Retries);
    }

    [Fact]
    public void AStrongMatch_AndAFacetOutOfView_AreNotRetried()
    {
        var matcher = new FakePhotoTextureMatcher();
        matcher.Views.Add(new FakeTextureView(10, 0, 0, 2000, 100, HoldPlacementScenario.Shift(99.5)));

        Assert.True(RegistrationAttempts.IsStrong(Register(matcher).Single()));
        Assert.False(Register(matcher, texture: 7).Single().Accepted);

        Assert.Empty(matcher.Retries);
    }

    [Fact]
    public void AWeakButAcceptedFacet_IsAlsoSeededFromAnchors()
    {
        var matcher = new FakePhotoTextureMatcher();
        matcher.Views.Add(new FakeTextureView(10, 0, 0, 2000, 200, HoldPlacementScenario.Shift(99.5)));
        var anchors = new List<PlaneAnchor>();
        for (var i = 0; i < 12; i++)
        {
            var (x, y) = (200 + (i % 4 * 500.0), 400 + (i / 4 * 1000.0));
            var (a, b) = Frame.ToPlane(x + 99.5, y + 99.5);
            anchors.Add(new PlaneAnchor(x / FakePhotoTextureMatcher.Width, y / FakePhotoTextureMatcher.Height, "0", a, b));
        }

        Assert.True(Register(matcher, TimeSpan.Zero, anchors: anchors).Single().Accepted);

        Assert.Contains(((byte)10, (byte)0), matcher.Seeded); // a weak registration is searched around the anchors' view too
    }

    private static List<FacetRegistration> Register(
        FakePhotoTextureMatcher matcher, TimeSpan? budget = null, byte texture = 0, List<PlaneAnchor>? anchors = null)
    {
        using var session = matcher.OpenPhoto([10]);
        var textures = new List<RegistrationTexture> { new(Frame, new PlaneRectMm(0, 2000, 0, 3000), [texture], null) };
        return new PhotoRegistrar(session, NullLogger.Instance, "c0 r0", anchors: anchors, attemptBudget: budget ?? TimeSpan.FromMinutes(1))
            .RegisterAll(textures);
    }
}
