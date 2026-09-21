namespace Blocwerk.Core.Services;

/// <summary>
/// The pure generation rules behind the boulder detail page's Then/Now decision. They live here,
/// not in the razor, because both of them were shipped wrong from inside a component that no test
/// can reach: which view a boulder opens in, and how many of its holds the historic view genuinely
/// could not draw. Nothing here touches the database or the render tree — the page is a thin caller.
/// </summary>
public static class BoulderGenerationView
{
    /// <summary>
    /// The centre panel (Col 0, Row 0) a hold with no panel id of its own would be drawn on, or null
    /// when the set has none.
    /// </summary>
    /// <remarks>
    /// A panel list can hold SEVERAL rows at (0,0) — a historic generation's read returns the panel
    /// rows of that generation alongside the ones carried forward — and
    /// <c>FirstOrDefault</c> then picked between them in whatever order the provider returned, so the
    /// same boulder could open on a different photo between two loads. The pick is therefore explicit
    /// and matches the discipline in <c>BoulderService.LoadPredecessorMapAsync</c>: the newest
    /// generation first (the panel that actually represents the view being drawn), then the smallest
    /// id — arbitrary, but identical on every read, machine and provider.
    /// </remarks>
    public static WallPanelInfo? CentrePanel(IEnumerable<WallPanelInfo> livePanels)
    {
        return livePanels
            .Where(p => p is { Col: 0, Row: 0 })
            .OrderByDescending(p => p.Generation)
            .ThenBy(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether these panels could actually DRAW a boulder made of <paramref name="holds"/>: true as
    /// soon as one hold lands somewhere honest, false when none does.
    /// </summary>
    /// <remarks>
    /// A hold with its own panel id renders when that panel is in the set. A hold WITHOUT one falls
    /// back to the centre panel — and that fallback only counts when the hold is not OLDER than the
    /// centre panel it would land on. An older hold's coordinates were measured on a photo that has
    /// since been replaced, so dropping it onto a re-photographed panel paints it over whatever
    /// features happen to be there now: it does not render here, it renders wrong. That is the
    /// Evening Wood shape — generation-0 holds with no panel id on a generation-3 wall — and calling
    /// it "renders here" is what opened that boulder in "Now" over the wrong photo.
    /// <para>
    /// An empty panel set is true: there is no panel viewer at all, so the single-image arm draws the
    /// boulder over the wall photo and always renders something.
    /// </para>
    /// </remarks>
    public static bool RendersOnPanels(
        IReadOnlyCollection<WallPanelInfo> livePanels,
        IEnumerable<HoldPlacement> holds)
    {
        if (livePanels.Count == 0)
        {
            return true;
        }

        var livePanelIds = livePanels.Select(p => p.Id).ToHashSet();
        var centre = CentrePanel(livePanels);

        foreach (var hold in holds)
        {
            if (hold.PanelId is { } panelId)
            {
                if (livePanelIds.Contains(panelId))
                {
                    return true;
                }

                continue;
            }

            if (centre is not null && hold.Generation >= centre.Generation)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How many of the boulder's holds the translation genuinely failed to map onto the generation
    /// being drawn.
    /// </summary>
    /// <remarks>
    /// Counted from the holds that came back UNMAPPED, never as <c>Count - translated.Count</c>:
    /// several of a boulder's holds can converge on one older row (that row was later recorded as
    /// becoming each of them), and subtracting set sizes reports the convergence as loss — "1 hold
    /// has no match" on a boulder whose every hold matched. See
    /// <see cref="BoulderHoldAtGeneration.SourceHoldIds"/>, which is what makes the difference
    /// visible at all.
    /// </remarks>
    public static int UntranslatedHoldCount(
        IEnumerable<Guid> boulderHoldIds,
        IEnumerable<BoulderHoldAtGeneration> translated)
    {
        var mapped = translated.SelectMany(t => t.SourceHoldIds).ToHashSet();
        return boulderHoldIds.Distinct().Count(id => !mapped.Contains(id));
    }
}
