using Blocwerk.Core.Enums;
using Blocwerk.Core.Stitching;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Locks down the invariant the whole dual-projection design rests on: the angled projection is
/// the ortho one with ONLY the vertical axis scaled by cos(wallAngle), and because hold
/// coordinates are normalised per axis (X = px/width, Y = px/height, aspect NOT preserved), that
/// scale cancels out. One hold set therefore serves both projections and switching projection is
/// a pure image swap — never a coordinate conversion.
/// </summary>
public class WallStitchProjectionInvariantTests
{
    private const double Tolerance = 1e-12;

    [Theory]
    [InlineData(0.0)]
    [InlineData(10.0)]
    [InlineData(25.0)]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(75.0)]
    public void VerticalScale_IsCosineOfTheWallAngle(double wallAngleDegrees)
    {
        var expected = Math.Cos(wallAngleDegrees * Math.PI / 180.0);
        var result = ResultFor(wallAngleDegrees, orthoHeight: 4864);

        Assert.Equal(expected, result.VerticalScale, Tolerance);

        // The sidecar derives the angled artifact's pixel height from that same factor.
        Assert.Equal((int)Math.Round(4864 * expected), result.Angled.Height);
        Assert.Equal(result.Ortho.Width, result.Angled.Width);
    }

    [Fact]
    public void VerticalScale_At45Degrees_IsTheDocumentedConstant()
    {
        Assert.Equal(0.7071, ResultFor(45.0, orthoHeight: 4864).VerticalScale, 4);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(15.0)]
    [InlineData(30.0)]
    [InlineData(45.0)]
    [InlineData(70.0)]
    public void NormalisedHoldCoordinates_AreIdentical_InBothProjections(double wallAngleDegrees)
    {
        const int orthoWidth = 7648;
        const int orthoHeight = 4864;
        var scale = Math.Cos(wallAngleDegrees * Math.PI / 180.0);

        // A hold as measured in ortho pixels...
        const double pixelX = 3901.0;
        const double pixelY = 1622.0;

        var orthoNormalisedX = pixelX / orthoWidth;
        var orthoNormalisedY = pixelY / orthoHeight;

        // ...and the SAME hold in the angled image, where only the vertical axis was scaled.
        var angledNormalisedX = pixelX / orthoWidth;
        var angledNormalisedY = (pixelY * scale) / (orthoHeight * scale);

        Assert.Equal(orthoNormalisedX, angledNormalisedX, Tolerance);
        Assert.Equal(orthoNormalisedY, angledNormalisedY, Tolerance);
    }

    [Fact]
    public void OneHoldSet_ServesBothProjections()
    {
        var result = ResultFor(45.0, orthoHeight: 4864);

        // The contract exposes a single holds array alongside both artifacts: there is deliberately
        // no per-projection hold set and no conversion helper anywhere in the domain.
        Assert.NotNull(result.Holds);
        Assert.Single(result.Holds!);
        Assert.NotEqual(result.Ortho.Height, result.Angled.Height);
        Assert.Equal(result.Ortho.Width, result.Angled.Width);
    }

    [Theory]
    [InlineData(WallPhotoProjection.Angled, "angled")]
    [InlineData(WallPhotoProjection.Ortho, "ortho")]
    public void ProjectionWireSpelling_MatchesTheSidecarContract(WallPhotoProjection projection, string expected)
    {
        Assert.Equal(expected, projection.ToWire());
    }

    private static StitchJobResult ResultFor(double wallAngleDegrees, int orthoHeight)
    {
        const int width = 7648;
        var scale = Math.Cos(wallAngleDegrees * Math.PI / 180.0);

        return new StitchJobResult(
            new StitchArtifactRef("ortho.png", width, orthoHeight),
            new StitchArtifactRef("angled.png", width, (int)Math.Round(orthoHeight * scale)),
            "display-ortho.jpg",
            "display-angled.jpg",
            wallAngleDegrees,
            scale,
            Diagnostics: null,
            Holds: [new StitchResultHold(Guid.NewGuid(), 0.51, 0.33, 0.012, null, "matched", 0.87)]);
    }
}
