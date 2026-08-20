using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The angle-change wedge derives its fold angles from the difference between the
/// surfaces meeting at each edge. These tests pin the two worked examples the tool
/// was designed around: a 45° wall stepped up to a vertical (90°) face, with and
/// without a 30° lower portion.
/// </summary>
public class WedgeCalculatorTests
{
    [Fact]
    public void TriangularPrism_FaceMeetsWall_FoldsByAngleDifference()
    {
        var result = WedgeCalculator.Calculate(
            wallAngleDeg: 45,
            targetAngleDeg: 90,
            faceWidth: 300,
            faceHeight: 400,
            thickness: 18);

        Assert.Equal(45, result.AngleChangeDeg, 3);
        Assert.Equal(45, result.FaceToWallFoldDeg, 3);
        Assert.Null(result.FaceToLowerFoldDeg);

        // Face, cap and two side panels.
        var face = result.Pieces.Single(p => p.Name == "Face");
        Assert.Equal(400, face.EdgeLengths[1], 3);   // side edge = face height
        Assert.Equal(45, face.EdgeBevelAngles[0], 3); // bottom edge folds onto the wall
        Assert.Equal(2, result.Pieces.Single(p => p.Name.StartsWith("Side")).Quantity);
    }

    [Fact]
    public void WithLowerPortion_FaceFoldsOntoLowerPortionAtDifference()
    {
        var result = WedgeCalculator.Calculate(
            wallAngleDeg: 45,
            targetAngleDeg: 90,
            faceWidth: 300,
            faceHeight: 400,
            thickness: 18,
            lowerPortionAngleDeg: 30,
            lowerPortionLength: 150);

        Assert.Equal(60, result.FaceToLowerFoldDeg!.Value, 3); // |90 - 30|
        Assert.Equal(15, result.LowerToWallFoldDeg!.Value, 3); // |30 - 45|

        var face = result.Pieces.Single(p => p.Name == "Face");
        Assert.Equal(60, face.EdgeBevelAngles[0], 3);

        var lower = result.Pieces.Single(p => p.Name == "Lower portion");
        Assert.Equal(150, lower.EdgeLengths[1], 3);
        Assert.Equal(15, lower.EdgeBevelAngles[0], 3);
        Assert.Equal(60, lower.EdgeBevelAngles[2], 3);

        // The side panel is now a four-sided profile (two merged wedges).
        var side = result.Pieces.Single(p => p.Name.StartsWith("Side"));
        Assert.Equal(4, side.FlatVertices.Length);
    }

    [Fact]
    public void OverallWidth_AddsAnEndPanelThicknessEachSide()
    {
        var result = WedgeCalculator.Calculate(45, 90, 300, 400, 18);
        Assert.Equal(336, result.OverallWidthMm, 3);
    }

    [Theory]
    [InlineData(90, 45)] // target shallower than wall
    [InlineData(45, 45)] // target equal to wall
    public void TargetMustBeSteeperThanWall(double wall, double target)
    {
        Assert.Throws<ArgumentException>(() =>
            WedgeCalculator.Calculate(wall, target, 300, 400, 18));
    }

    [Fact]
    public void LowerPortionAngle_MustBeShallowerThanTarget()
    {
        Assert.Throws<ArgumentException>(() =>
            WedgeCalculator.Calculate(45, 90, 300, 400, 18, lowerPortionAngleDeg: 95, lowerPortionLength: 150));
    }
}
