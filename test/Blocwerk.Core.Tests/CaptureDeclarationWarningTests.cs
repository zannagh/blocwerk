// <copyright file="CaptureDeclarationWarningTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A named segment row without an angle and without the gravity flag is left out of the solve, so its markers
/// merge into the nearest face. That must never happen silently: the row warns, and the API's start answer says so.
/// Unnamed spare rows (the documented way to merge filler markers) stay quiet.
/// </summary>
public class CaptureDeclarationWarningTests
{
    [Fact]
    public void ANamedRowWithoutAngleOrGravity_Warns()
    {
        var warnings = CaptureDeclarationRules.MergeWarnings(Declarations(new CaptureSegmentDeclaration(5, "Cave", null, false)));

        var warning = Assert.Single(warnings);
        Assert.StartsWith("Segment 5 (“Cave”) has no angle, so its markers will be merged into the nearest wall face.", warning);
    }

    [Theory]
    [InlineData("Segment 5", null, false)]
    [InlineData("", null, false)]
    [InlineData("  ", null, false)]
    [InlineData("Cave", 30.0, false)]
    [InlineData("Cave", 0.0, false)]
    [InlineData("Cave", null, true)]
    public void SpareRowsAndDeclaredRows_StayQuiet(string name, double? angle, bool vertical)
    {
        var row = new CaptureSegmentDeclaration(5, name, angle, vertical);

        Assert.Empty(CaptureDeclarationRules.MergeWarnings(Declarations(row)));
        Assert.Null(CaptureDeclarationRules.MergeWarningFor(row));
    }

    [Fact]
    public void TheTableRow_ShowsTheWarning_UntilAnAngleIsGiven()
    {
        var row = new CaptureDeclarationRow { Index = 5, Name = "Cave" };
        Assert.NotNull(row.MergeWarning);

        row.AngleDeg = 20;
        Assert.Null(row.MergeWarning);

        row.AngleDeg = null;
        row.Vertical = true;
        Assert.Null(row.MergeWarning);

        // A row whose name was cleared falls back to "Segment 5": a spare row, merged on purpose.
        row.Vertical = false;
        row.Name = string.Empty;
        Assert.Null(row.MergeWarning);
    }

    [Fact]
    public void AWarning_DoesNotBlockTheStart()
    {
        var declarations = Declarations(new CaptureSegmentDeclaration(5, "Cave", null, false));

        Assert.Empty(CaptureDeclarationRules.Validate(declarations, new HashSet<int> { 5 }));
    }

    [Fact]
    public void LevelPairsFromTheApi_AreChecked()
    {
        var bad = new CaptureDeclarations([new CaptureSegmentDeclaration(0, "main", 30, false)], [[14], [3, 3]]);
        var good = new CaptureDeclarations([new CaptureSegmentDeclaration(0, "main", 30, false)], [[14, 15]]);

        Assert.Contains(
            "Each level pair must name two different marker ids, e.g. [14, 15].", CaptureDeclarationRules.Validate(bad, null));
        Assert.Empty(CaptureDeclarationRules.Validate(good, null));
    }

    private static CaptureDeclarations Declarations(params CaptureSegmentDeclaration[] segments) =>
        new([new CaptureSegmentDeclaration(0, "main wall", 30, true), .. segments], []);
}
