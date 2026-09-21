using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Covers the "missing information" predicate behind the wall editor's <kbd>i</kbd> overlay.
/// The overlay must not lie: it may only report fields that can actually be told apart from a
/// never-touched default, which is colour and (on hand holds only) the grip sub-type. And it must be
/// USEFUL, which is why the criteria are chosen rather than fixed — nothing ever stamps a grip
/// sub-type, so folding it in unconditionally badged nearly every hand hold on a mature wall.
/// </summary>
public class HoldCompletenessTests
{
    // Every criterion at once — the strictest predicate, which most of these cases exercise.
    private const HoldCompletenessCriteria Both =
        HoldCompletenessCriteria.Color | HoldCompletenessCriteria.HandType;

    private static Hold Complete() => new()
    {
        Color = "yellow",
        Category = HoldCategory.Hand,
        HandType = HoldHandType.Jug,
        Material = HoldMaterial.PU,
    };

    [Fact]
    public void FullyFilledHandHold_IsComplete()
    {
        Assert.False(HoldCompleteness.IsIncomplete(Complete(), Both));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingColor_IsIncomplete(string? color)
    {
        var hold = Complete();
        hold.Color = color;

        Assert.True(HoldCompleteness.MissingColor(hold));
        Assert.True(HoldCompleteness.IsIncomplete(hold, Both));
    }

    [Fact]
    public void HandHoldWithoutSubType_IsIncomplete()
    {
        var hold = Complete();
        hold.HandType = null;

        Assert.True(HoldCompleteness.MissingHandType(hold));
        Assert.True(HoldCompleteness.IsIncomplete(hold, Both));
    }

    [Fact]
    public void FootHoldWithoutSubType_IsComplete()
    {
        var hold = Complete();
        hold.Category = HoldCategory.Foot;
        hold.HandType = null;

        Assert.False(HoldCompleteness.MissingHandType(hold));
        Assert.False(HoldCompleteness.IsIncomplete(hold, Both));
    }

    [Fact]
    public void MissingMaterial_IsNotReported()
    {
        var hold = Complete();
        hold.Material = null;

        Assert.False(HoldCompleteness.IsIncomplete(hold, Both));
    }

    [Fact]
    public void DefaultCategory_IsNotReported()
    {
        // Hand == 0 is both the enum default and a valid deliberate value, so a hold that only
        // ever had the default category must not be flagged on that basis alone.
        var hold = Complete();
        hold.Category = default;

        Assert.False(HoldCompleteness.IsIncomplete(hold, Both));
    }

    [Fact]
    public void MissingShape_IsReportedSeparately_AndNeverFoldedIn()
    {
        var hold = Complete();
        hold.ShapePoints = null;
        Assert.True(HoldCompleteness.MissingShape(hold));
        Assert.False(HoldCompleteness.IsIncomplete(hold, Both));

        hold.ShapePoints = [new ShapePoint { Dx = 0, Dy = 0 }, new ShapePoint { Dx = 0.1, Dy = 0 }];
        Assert.True(HoldCompleteness.MissingShape(hold));

        hold.ShapePoints.Add(new ShapePoint { Dx = 0.1, Dy = 0.1 });
        Assert.False(HoldCompleteness.MissingShape(hold));
    }

    [Fact]
    public void CountIncomplete_CountsOnlyTheIncompleteOnes()
    {
        var holds = new List<Hold>
        {
            Complete(),
            new() { Category = HoldCategory.Hand, HandType = HoldHandType.Crimp },
            new() { Category = HoldCategory.Foot, Color = "blue" },
            new() { Category = HoldCategory.Hand, Color = "red" },
        };

        Assert.Equal(2, HoldCompleteness.CountIncomplete(holds, Both));
    }

    // The default is colour ALONE. A hand hold with no sub-type is extremely common — nothing stamps
    // one — so badging it by default made the overlay a sea of "?" that could never be worked down.
    [Fact]
    public void Default_ReportsMissingColourOnly()
    {
        Assert.Equal(HoldCompletenessCriteria.Color, HoldCompleteness.Default);

        var noSubType = Complete();
        noSubType.HandType = null;
        Assert.False(HoldCompleteness.IsIncomplete(noSubType, HoldCompleteness.Default));

        var noColour = Complete();
        noColour.Color = null;
        Assert.True(HoldCompleteness.IsIncomplete(noColour, HoldCompleteness.Default));
    }

    [Fact]
    public void NoCriteria_BadgesNothing()
    {
        var hold = new Hold { Category = HoldCategory.Hand };

        Assert.False(HoldCompleteness.IsIncomplete(hold, HoldCompletenessCriteria.None));
        Assert.Equal(0, HoldCompleteness.CountIncomplete([hold], HoldCompletenessCriteria.None));
    }

    [Fact]
    public void Toggle_CyclesOffThenColourThenBoth()
    {
        var off = HoldCompletenessCriteria.None;
        var colour = HoldCompleteness.Next(off);
        var both = HoldCompleteness.Next(colour);

        Assert.Equal(HoldCompletenessCriteria.Color, colour);
        Assert.Equal(Both, both);
        Assert.Equal(HoldCompletenessCriteria.None, HoldCompleteness.Next(both));
    }

    [Fact]
    public void HandTypeCriterion_AloneIgnoresColour()
    {
        var hold = Complete();
        hold.Color = null;

        Assert.False(HoldCompleteness.IsIncomplete(hold, HoldCompletenessCriteria.HandType));
    }
}
