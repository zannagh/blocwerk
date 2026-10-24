// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="GradeScale.IsFont"/> is what the grade picker opens on: a stored Font grade must open the Font scale, or
/// the picker shows the V-Scale with nothing selected (the boulder page's inline editor on a "6B").
/// </summary>
public class GradeScaleIsFontTests
{
    [Theory]
    [InlineData("6B", true)]
    [InlineData("3", true)]
    [InlineData("8C", true)]
    [InlineData("V4-", false)]
    [InlineData("VB", false)]
    [InlineData("V15", false)]
    public void AKnownGrade_NamesItsScale(string grade, bool font)
    {
        Assert.Equal(font, GradeScale.IsFont(grade));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("5c")]
    public void AnEmptyOrUnknownGrade_HasNoScale(string? grade)
    {
        Assert.Null(GradeScale.IsFont(grade));
    }

    [Fact]
    public void TheTwoScales_ShareNoGrade()
    {
        Assert.Empty(GradeScale.FontGrades.Intersect(GradeScale.VGrades));
    }
}
