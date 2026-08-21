using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The angle-change wedge is a single triangle: the face meets the wall, runs out
/// to the furthest corner, and a return (the "lower portion") folds back to the
/// wall. Fold angles are the difference between the surfaces meeting at each edge,
/// and the return length is implicit. These tests pin the worked examples: a 45°
/// wall stepped up to a vertical (90°) face, with and without a 30° lower portion.
/// </summary>
public class WedgeCalculatorTests
{
    [Fact]
    public void SimpleWedge_FaceFoldsOntoWallByAngleDifference()
    {
        var result = WedgeCalculator.Calculate(
            wallAngleDeg: 45,
            targetAngleDeg: 90,
            faceWidth: 300,
            faceHeight: 400,
            thickness: 18);

        Assert.Equal(45, result.AngleChangeDeg, 3);
        Assert.Null(result.FaceToLowerFoldDeg);

        // No explicit lower portion => horizontal return, which comes out exactly
        // face-sized (the isosceles case).
        Assert.Equal(400, result.LowerPortionLengthMm, 2);

        var face = result.Pieces.Single(p => p.Name == "Face");
        Assert.Equal(45, face.EdgeBevelAngles[0], 3); // face folds onto the wall

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
            lowerPortionAngleDeg: 30);

        Assert.Equal(60, result.FaceToLowerFoldDeg!.Value, 3); // |90 - 30|
        Assert.Equal(15, result.LowerToWallFoldDeg!.Value, 3); // |45 - 30|

        // Implicit length: Fh * sin(45) / sin(15).
        var expectedLength = 400.0 * Math.Sin(Deg2Rad(45)) / Math.Sin(Deg2Rad(15));
        Assert.Equal(expectedLength, result.LowerPortionLengthMm, 2);

        var face = result.Pieces.Single(p => p.Name == "Face");
        Assert.Equal(60, face.EdgeBevelAngles[2], 3); // far edge folds onto the lower portion

        var lower = result.Pieces.Single(p => p.Name == "Lower portion");
        Assert.Equal(15, lower.EdgeBevelAngles[0], 3);
        Assert.Equal(60, lower.EdgeBevelAngles[2], 3);
    }

    [Fact]
    public void HorizontalReturn_MatchesFaceSize_ForAnyFaceHeight()
    {
        // The horizontal (no-lower) return always comes out exactly face-sized
        // on a 45° wall to a vertical face, independent of the face height.
        var result = WedgeCalculator.Calculate(45, 90, 300, 500, 18);
        Assert.Equal(500, result.LowerPortionLengthMm, 2);
    }

    [Fact]
    public void OverallWidth_AddsAnEndPanelThicknessEachSide()
    {
        var result = WedgeCalculator.Calculate(45, 90, 300, 400, 18);
        Assert.Equal(336, result.OverallWidthMm, 3);
    }

    [Fact]
    public void TargetEqualToWall_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            WedgeCalculator.Calculate(45, 45, 300, 400, 18));
    }

    [Theory]
    [InlineData(45, 90, 60)] // face and lower both steeper than the wall
    [InlineData(45, 90, 50)] // the user's example: both steeper => never returns
    [InlineData(45, 30, 20)] // face and lower both shallower than the wall
    public void FaceAndLowerOnSameSideOfWall_AreRejected(double wall, double face, double lower)
    {
        // If the face and the lower portion sit on the same side of the wall angle
        // the panels diverge and the lower portion never returns to the wall.
        Assert.Throws<ArgumentException>(() =>
            WedgeCalculator.Calculate(wall, face, 300, 400, 18, lowerPortionAngleDeg: lower));
    }

    [Fact]
    public void ShallowFace_WithSteeperLower_IsAllowed()
    {
        // Wall 45°, resulting face 30° (shallower), lower portion 50° (steeper):
        // the two straddle the wall, so the triangle closes.
        var result = WedgeCalculator.Calculate(45, 30, 300, 400, 18, lowerPortionAngleDeg: 50);

        Assert.Equal(15, result.Pieces.Single(p => p.Name == "Face").EdgeBevelAngles[0], 3); // |30 - 45|
        Assert.Equal(5, result.LowerToWallFoldDeg!.Value, 3);   // |50 - 45|
        Assert.Equal(20, result.FaceToLowerFoldDeg!.Value, 3);  // |50 - 30|
    }

    [Fact]
    public void HorizontalLowerPortion_IsAllowed_ForSteeperFace()
    {
        // 0° (horizontal) lower portion is now selectable when the face is steeper
        // than the wall; it matches the implicit no-lower base.
        var withZero = WedgeCalculator.Calculate(45, 90, 300, 400, 18, lowerPortionAngleDeg: 0);
        var noLower = WedgeCalculator.Calculate(45, 90, 300, 400, 18);

        Assert.Equal(noLower.LowerPortionLengthMm, withZero.LowerPortionLengthMm, 3);
        Assert.Equal(400, withZero.LowerPortionLengthMm, 2);
    }

    private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;
}
