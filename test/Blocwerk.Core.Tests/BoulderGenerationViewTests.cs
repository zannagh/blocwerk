using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The generation rules the boulder detail page opens on. They were extracted out of the razor
/// precisely because BOTH shipped regressions lived there and no test could reach them: which view a
/// boulder opens in, and how many of its holds the historic view genuinely could not draw.
/// </summary>
public class BoulderGenerationViewTests
{
    private static readonly Guid CentreId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SideId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherCentreId = new("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void HoldsOlderThanTheCentrePanel_DoNotRenderOnIt()
    {
        // The Evening Wood shape: 11 holds at generation 0 with NO panel id, on a wall whose panels
        // have since been re-photographed at generation 3. The centre-panel fallback would happily
        // drop them onto today's photo — over features that no longer exist — so this must say no
        // and let the page open in "Then".
        var panels = new[] { Panel(CentreId, 0, 0, generation: 3) };
        var holds = Enumerable.Range(0, 11).Select(_ => new HoldPlacement(null, 0));

        Assert.False(BoulderGenerationView.RendersOnPanels(panels, holds));
    }

    [Fact]
    public void HoldsAtTheCentrePanelsGeneration_RenderOnIt()
    {
        // The other half of the rule: a hold with no panel id that is NOT older than the centre panel
        // still renders there — that fallback is what keeps never-stamped virtual holds visible.
        var panels = new[] { Panel(CentreId, 0, 0, generation: 3) };

        Assert.True(BoulderGenerationView.RendersOnPanels(panels, [new HoldPlacement(null, 3)]));
    }

    [Fact]
    public void HoldOnALivePanel_RendersWhateverItsGeneration()
    {
        // The generation test applies ONLY to the centre-panel fallback. A hold that names a panel
        // still in the set is drawn on its own photo, so its generation never enters the question.
        var panels = new[] { Panel(CentreId, 0, 0, generation: 3), Panel(SideId, 1, 0, generation: 3) };

        Assert.True(BoulderGenerationView.RendersOnPanels(panels, [new HoldPlacement(SideId, 0)]));
    }

    [Fact]
    public void HoldOnAPanelThatIsGone_DoesNotRender()
    {
        var panels = new[] { Panel(CentreId, 0, 0, generation: 3) };
        var superseded = new Guid("44444444-4444-4444-4444-444444444444");

        Assert.False(BoulderGenerationView.RendersOnPanels(panels, [new HoldPlacement(superseded, 2)]));
    }

    [Fact]
    public void NoPanelsAtAll_Renders()
    {
        // No panel viewer: the single-image arm draws the boulder over the wall photo and always
        // renders something, so "Then" must not be forced on a plain wall.
        Assert.True(BoulderGenerationView.RendersOnPanels([], [new HoldPlacement(null, 0)]));
    }

    [Fact]
    public void CentrePanel_IsTheSamePanelWhicheverOrderTheRowsArrive()
    {
        // Two rows tied at (0,0). FirstOrDefault picked between them in provider order, so the photo
        // a panel-less hold was drawn on could differ between two identical loads.
        var a = Panel(CentreId, 0, 0, generation: 0);
        var b = Panel(OtherCentreId, 0, 0, generation: 0);

        var forward = BoulderGenerationView.CentrePanel([a, b]);
        var reversed = BoulderGenerationView.CentrePanel([b, a]);

        Assert.Equal(forward?.Id, reversed?.Id);
        Assert.Equal(CentreId, forward?.Id);
    }

    [Fact]
    public void CentrePanel_PrefersTheNewestGenerationOverAnOlderRowAtTheSamePosition()
    {
        var old = Panel(CentreId, 0, 0, generation: 0);
        var current = Panel(OtherCentreId, 0, 0, generation: 3);

        Assert.Equal(OtherCentreId, BoulderGenerationView.CentrePanel([old, current])?.Id);
    }

    [Fact]
    public void ConvergedHolds_AreNotCountedAsMissing()
    {
        // The documented merge case: two of today's holds descend from ONE older row. Nothing is
        // missing, yet "BoulderHolds.Count - translated.Count" reported "1 has no match".
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var translated = new[] { Translated([first, second]) };

        Assert.Equal(0, BoulderGenerationView.UntranslatedHoldCount([first, second], translated));
    }

    [Fact]
    public void HoldWithNoAncestor_IsCountedAsMissing()
    {
        // The note must still fire when a hold genuinely has no row at that generation.
        var translated = new[] { Translated([Guid.NewGuid()]) };

        Assert.Equal(1, BoulderGenerationView.UntranslatedHoldCount(
            [translated[0].SourceHoldIds[0], Guid.NewGuid()], translated));
    }

    [Fact]
    public void NothingTranslated_CountsEveryHold()
    {
        Assert.Equal(2, BoulderGenerationView.UntranslatedHoldCount(
            [Guid.NewGuid(), Guid.NewGuid()], []));
    }

    private static WallPanelInfo Panel(Guid id, int col, int row, int generation) =>
        new(id, col, row, IsLive: true, HasStaged: false, generation, IsOutdated: false);

    private static BoulderHoldAtGeneration Translated(IReadOnlyList<Guid> sources) =>
        new(new Hold { Generation = 0 }, HoldType.Normal, HoldUsage.HandAndFoot)
        {
            SourceHoldIds = sources,
        };
}
