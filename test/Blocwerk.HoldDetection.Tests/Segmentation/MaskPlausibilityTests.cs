// <copyright file="MaskPlausibilityTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.HoldDetection.Segmentation;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Segmentation;

/// <summary>A hold is a solid body: thin spikes go, narrow slits close, and a hold that is thin as a whole stays.</summary>
public class MaskPlausibilityTests
{
    [Fact]
    public void AThinSpike_IsTakenOff()
    {
        using Mat mask = Blank();
        Cv2.Circle(mask, new Point(100, 100), 40, Scalar.All(255), -1);
        Cv2.Rectangle(mask, new Rect(138, 98, 50, 4), Scalar.All(255), -1);

        MaskPlausibility.Clean(mask);

        Assert.Equal(0, mask.At<byte>(100, 175));
        Assert.Equal(255, mask.At<byte>(100, 100));
        Assert.InRange(Cv2.CountNonZero(mask), 0.95 * Math.PI * 1600, 1.05 * Math.PI * 1600);
    }

    [Fact]
    public void ANarrowSlit_IsClosed()
    {
        using Mat mask = Blank();
        Cv2.Circle(mask, new Point(100, 100), 40, Scalar.All(255), -1);
        Cv2.Rectangle(mask, new Rect(98, 60, 4, 45), Scalar.All(0), -1);

        MaskPlausibility.Clean(mask);

        Assert.Equal(255, mask.At<byte>(90, 100));
    }

    [Fact]
    public void ARail_ThatIsThinAsAWhole_KeepsItsLength()
    {
        using Mat mask = Blank();
        Cv2.Rectangle(mask, new Rect(20, 95, 160, 10), Scalar.All(255), -1);
        int before = Cv2.CountNonZero(mask);

        MaskPlausibility.Clean(mask);

        Assert.InRange(Cv2.CountNonZero(mask), 0.9 * before, 1.05 * before);
        Assert.Equal(255, mask.At<byte>(100, 30));
        Assert.Equal(255, mask.At<byte>(100, 170));
    }

    [Fact]
    public void ARealConcaveShape_IsNotFilledIn()
    {
        // A crescent: the bay is as wide as the hold, so it is the hold's shape and not a slit.
        using Mat mask = Blank();
        Cv2.Circle(mask, new Point(100, 100), 60, Scalar.All(255), -1);
        Cv2.Circle(mask, new Point(100, 60), 45, Scalar.All(0), -1);

        MaskPlausibility.Clean(mask);

        Assert.Equal(0, mask.At<byte>(60, 100));
    }

    [Fact]
    public void ANotchCutIntoAnEdge_IsFilled()
    {
        using Mat mask = Blank();
        Cv2.Rectangle(mask, new Rect(50, 60, 100, 80), Scalar.All(255), -1);
        Cv2.Rectangle(mask, new Rect(90, 60, 16, 14), Scalar.All(0), -1);

        MaskPlausibility.Clean(mask);

        Assert.Equal(255, mask.At<byte>(66, 98));
    }

    private static Mat Blank() => new(200, 200, MatType.CV_8UC1, Scalar.All(0));
}
