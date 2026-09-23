// <copyright file="MarkerPlanPdfLayout.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>What one PDF page shows.</summary>
internal enum MarkerPdfPageKind
{
    /// <summary>Title, photo setup, placement map, instructions.</summary>
    Overview,

    /// <summary>The placement table (a slice of the markers).</summary>
    Table,

    /// <summary>Markers at true size.</summary>
    Markers,
}

/// <summary>
/// One marker's cut box on a marker page; page mm, y DOWN, box = marker plus white border (the quiet
/// zone, or the room the mounting holes need).
/// </summary>
internal sealed record MarkerTile(PlanMarker Marker, double LeftMm, double TopMm, double BorderMm)
{
    public double BoxMm => Marker.SizeMm + (2 * BorderMm);

    public double SquareLeftMm => LeftMm + BorderMm;

    public double SquareTopMm => TopMm + BorderMm;
}

/// <summary>A page: its paper size (mm), kind, and content slice.</summary>
internal sealed record MarkerPdfPage(
    double WidthMm, double HeightMm, MarkerPdfPageKind Kind, IReadOnlyList<MarkerTile> Tiles, int TableStart, int TableCount);

/// <summary>
/// Paginates the PDF. Marker pages flow every marker (by id) at TRUE size onto A4 portrait, falling
/// back to A3 — or a custom sheet — only for markers too big for A4. Each marker keeps a white quiet
/// zone of one module (a sixth of its side), shrunk to no less than half a module when that is what
/// lets it fit on A4. With mounting holes the border is whatever <see cref="MountingHoleLayout"/> needs
/// (the head plus its gaps, never shrunk), which is usually far thinner than the quiet zone.
/// </summary>
internal static class MarkerPlanPdfLayout
{
    public const double MarginMm = 10;
    public const double HeaderMm = 14;
    public const double FooterMm = 22;
    public const double LabelAboveMm = 5;
    public const double LabelBelowMm = 9;
    public const double GapMm = 4;
    public const int TableRowsPerPage = 48;

    private static readonly (double W, double H)[] Papers = [(210, 297), (297, 420)];

    public static List<MarkerPdfPage> Build(MarkerPlan plan)
    {
        var pages = new List<MarkerPdfPage> { new(210, 297, MarkerPdfPageKind.Overview, [], 0, 0) };
        for (var start = 0; start < plan.Markers.Count; start += TableRowsPerPage)
        {
            pages.Add(new MarkerPdfPage(210, 297, MarkerPdfPageKind.Table, [], start, Math.Min(TableRowsPerPage, plan.Markers.Count - start)));
        }

        var holes = Holes(plan);
        pages.AddRange(MarkerPages(plan.Markers.Where(m => double.IsFinite(m.SizeMm) && m.SizeMm > 0).OrderBy(m => m.Id), holes));
        return pages;
    }

    /// <summary>The mounting holes to print, or null when the plan prints no (valid) holes.</summary>
    public static MountingHoles? Holes(MarkerPlan plan) =>
        plan.Print?.MountingHoles is { Enabled: true } holes && holes.Problems().Count == 0 ? holes : null;

    private static List<MarkerPdfPage> MarkerPages(IEnumerable<PlanMarker> markers, MountingHoles? holes)
    {
        var pages = new List<MarkerPdfPage>();
        (double W, double H) paper = default;
        List<MarkerTile>? tiles = null;
        double x = 0, y = 0, rowHeight = 0;
        foreach (var marker in markers)
        {
            var (fitPaper, border) = holes is not null ? PaperWithHoles(marker.SizeMm, holes) : PaperFor(marker.SizeMm);
            var box = marker.SizeMm + (2 * border);
            var tileH = LabelAboveMm + box + LabelBelowMm;
            if (tiles is null || fitPaper != paper || !FitsOnPage(paper, ref x, ref y, ref rowHeight, box, tileH))
            {
                paper = fitPaper;
                tiles = [];
                pages.Add(new MarkerPdfPage(paper.W, paper.H, MarkerPdfPageKind.Markers, tiles, 0, 0));
                (x, y, rowHeight) = (MarginMm, MarginMm + HeaderMm, 0);
            }

            tiles.Add(new MarkerTile(marker, x, y + LabelAboveMm, border));
            x += box + GapMm;
            rowHeight = Math.Max(rowHeight, tileH);
        }

        return pages;
    }

    /// <summary>Moves to the next row when needed; false when the tile no longer fits on this page.</summary>
    private static bool FitsOnPage((double W, double H) paper, ref double x, ref double y, ref double rowHeight, double box, double tileH)
    {
        if (x + box > paper.W - MarginMm)
        {
            x = MarginMm;
            y += rowHeight + GapMm;
            rowHeight = 0;
        }

        return y + tileH <= paper.H - MarginMm - FooterMm;
    }

    /// <summary>The smallest paper a marker fits on (A4, A3, else a custom sheet) and its quiet zone.</summary>
    private static ((double W, double H) Paper, double QuietMm) PaperFor(double sizeMm)
    {
        var module = sizeMm / ArucoDict4X4.ModulesPerSide;
        foreach (var paper in Papers)
        {
            var width = paper.W - (2 * MarginMm);
            var height = paper.H - (2 * MarginMm) - HeaderMm - FooterMm - LabelAboveMm - LabelBelowMm;
            var quiet = Math.Min(module, Math.Min(width - sizeMm, height - sizeMm) / 2);
            if (quiet >= module / 2)
            {
                return (paper, quiet);
            }
        }

        return (CustomSheet(sizeMm, module), module);
    }

    /// <summary>The smallest paper that fits the marker with its full mounting-hole border.</summary>
    private static ((double W, double H) Paper, double BorderMm) PaperWithHoles(double sizeMm, MountingHoles holes)
    {
        var border = MountingHoleLayout.BorderMm(holes);
        var box = sizeMm + (2 * border);
        foreach (var paper in Papers)
        {
            var width = paper.W - (2 * MarginMm);
            var height = paper.H - (2 * MarginMm) - HeaderMm - FooterMm - LabelAboveMm - LabelBelowMm;
            if (box <= width && box <= height)
            {
                return (paper, border);
            }
        }

        return (CustomSheet(sizeMm, border), border);
    }

    private static (double W, double H) CustomSheet(double sizeMm, double borderMm)
    {
        var side = Math.Ceiling(sizeMm + (2 * borderMm) + (2 * MarginMm) + 20);
        return (side, side + HeaderMm + FooterMm + LabelAboveMm + LabelBelowMm);
    }
}
