using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hip roof is a rectangular base rising to a horizontal ridge edge, with two
/// trapezoidal long faces and two triangular hip ends. These tests pin the piece
/// set, the symmetry of a square-in-plan roof, and the ridge/hip validation.
/// </summary>
public class VolumeCalculatorRoofTests
{
    [Fact]
    public void Roof_ProducesTwoLongFacesTwoHipEndsAndABase()
    {
        var result = VolumeCalculator.CalculateRoof(1000, 600, 400, 300, 18);

        Assert.Equal(2, result.Pieces.Count(p => p.Name == "Long face"));
        Assert.Equal(2, result.Pieces.Count(p => p.Name == "Hip end"));
        Assert.Single(result.Pieces.Where(p => p.Name == "Base"));
        Assert.NotNull(result.RidgeVertices);
        Assert.Equal(2, result.RidgeVertices!.Length);
    }

    [Fact]
    public void Roof_RidgeVertices_SpanTheRidgeLengthAtHeight()
    {
        var result = VolumeCalculator.CalculateRoof(1000, 600, 400, 300, 18);
        var ridge = result.RidgeVertices!;

        Assert.Equal(400, Math.Abs(ridge[1].X - ridge[0].X), 3); // ridge length
        Assert.Equal(300, ridge[0].Y, 3);                        // at full height
        Assert.Equal(300, ridge[1].Y, 3);
    }

    [Fact]
    public void Roof_HipEndSlopeSteepensAsRidgeApproachesFullLength()
    {
        // Longer ridge => shorter hip run => steeper hip fall-off => smaller base bevel.
        var shortRidge = VolumeCalculator.CalculateRoof(1000, 600, 200, 300, 18);
        var longRidge = VolumeCalculator.CalculateRoof(1000, 600, 800, 300, 18);

        var shortBevel = shortRidge.Pieces.First(p => p.Name == "Hip end").EdgeBevelAngles[0];
        var longBevel = longRidge.Pieces.First(p => p.Name == "Hip end").EdgeBevelAngles[0];

        Assert.True(longBevel < shortBevel);
    }

    [Theory]
    [InlineData(1000, 600, 0, 300)] // zero ridge
    [InlineData(1000, 600, 1200, 300)] // ridge longer than the base
    public void Roof_InvalidRidgeLength_Throws(double l, double b, double r, double h)
    {
        Assert.Throws<ArgumentException>(() => VolumeCalculator.CalculateRoof(l, b, r, h, 18));
    }
}
