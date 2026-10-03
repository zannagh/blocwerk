using Blocwerk.HoldDetection.Tiling;

namespace Blocwerk.HoldDetection.Tests.Tiling;

public class TileBoxMergerTests
{
    private const int W = 4032;
    private const int H = 3024;

    // Two horizontally neighbouring windows sharing x = 1024..1280.
    private static readonly TileWindow Left = new(0, 0, 1280, 1280);
    private static readonly TileWindow Right = new(1024, 0, 1280, 1280);

    [Fact]
    public void Box_touching_an_inner_edge_is_cut_but_one_on_the_image_border_is_kept()
    {
        var cutRight = new PixelBox(1240, 500, 1278, 540, 0.9, "hold");
        var onImageLeftBorder = new PixelBox(0, 500, 40, 540, 0.9, "hold");
        var onImageTopBorder = new PixelBox(500, 0, 540, 30, 0.9, "hold");

        Assert.True(TileBoxMerger.TouchesInnerEdge(cutRight, Left, W, H));
        Assert.False(TileBoxMerger.TouchesInnerEdge(onImageLeftBorder, Left, W, H));
        Assert.False(TileBoxMerger.TouchesInnerEdge(onImageTopBorder, Left, W, H));

        // The right window's left edge is inner: a box starting at its x = 2 is cut.
        Assert.True(TileBoxMerger.TouchesInnerEdge(new PixelBox(2, 500, 40, 540, 0.9, "hold"), Right, W, H));
    }

    [Fact]
    public void Last_window_edges_on_the_image_border_never_cut()
    {
        var corner = new TileWindow(W - 1280, H - 1280, 1280, 1280);
        var box = new PixelBox(1240, 1240, 1280, 1280, 0.9, "hold");

        Assert.False(TileBoxMerger.TouchesInnerEdge(box, corner, W, H));
    }

    [Fact]
    public void Hold_split_across_two_windows_comes_out_once_at_its_whole_extent()
    {
        // Physical hold at x = 1250..1310: the left window sees it cut at its right edge, the right window whole.
        var leftLocal = new PixelBox(1250, 600, 1279, 660, 0.7, "hold");
        var rightLocal = new PixelBox(226, 600, 286, 660, 0.8, "hold");

        var all = TileBoxMerger.ToImage([leftLocal], Left, W, H)
            .Concat(TileBoxMerger.ToImage([rightLocal], Right, W, H));
        var merged = TileBoxMerger.Merge(all);

        var hold = Assert.Single(merged);
        Assert.Equal(new PixelBox(1250, 600, 1310, 660, 0.8, "hold"), hold);
    }

    [Fact]
    public void Hold_seen_whole_by_both_overlapping_windows_is_merged_keeping_the_higher_confidence()
    {
        var a = new PixelBox(1100, 600, 1160, 660, 0.6, "hold");
        var b = new PixelBox(1102, 603, 1161, 662, 0.9, "hold");
        var c = new PixelBox(1098, 598, 1158, 671, 0.5, "hold");

        var merged = TileBoxMerger.Merge([a, b, c]);

        Assert.Equal([b], merged);
    }

    [Fact]
    public void Smaller_partial_box_centred_inside_a_kept_box_is_a_duplicate()
    {
        var whole = new PixelBox(1000, 1000, 1100, 1100, 0.9, "hold");
        var partial = new PixelBox(1030, 1010, 1100, 1060, 0.5, "hold"); // IoU 0.35, centre inside

        Assert.Equal([whole], TileBoxMerger.Merge([partial, whole]));
    }

    [Fact]
    public void Neighbouring_distinct_holds_both_survive()
    {
        var a = new PixelBox(1000, 1000, 1040, 1040, 0.9, "hold");
        var b = new PixelBox(1045, 1000, 1085, 1040, 0.8, "hold");

        Assert.Equal(2, TileBoxMerger.Merge([a, b]).Count);
    }

    [Fact]
    public void Nms_is_class_aware()
    {
        var hold = new PixelBox(1000, 1000, 1100, 1100, 0.9, "hold");
        var volume = new PixelBox(1000, 1000, 1100, 1100, 0.8, "volume");

        Assert.Equal(2, TileBoxMerger.Merge([hold, volume]).Count);
    }

    [Fact]
    public void Lattice_of_mutually_overlapping_boxes_on_plain_texture_is_dropped_but_real_holds_survive()
    {
        // A 3x3 lattice of 150 px boxes 90 px apart (pairwise IoU 0.25) - the crash-mat hallucination.
        var lattice = Enumerable.Range(0, 9)
            .Select(i => new PixelBox(2000 + (i % 3 * 90), 2500 + (i / 3 * 90), 2150 + (i % 3 * 90), 2650 + (i / 3 * 90), 0.95, "hold"))
            .ToList();
        var isolated = new PixelBox(500, 500, 560, 560, 0.9, "hold");
        var bigHold = new PixelBox(1000, 1000, 1200, 1150, 0.9, "hold");
        var footOnIt = new PixelBox(1150, 1100, 1180, 1130, 0.6, "hold");
        var pairNeighbour = new PixelBox(530, 520, 600, 590, 0.7, "hold");

        var kept = TileBoxMerger.DropTangles([.. lattice, isolated, bigHold, footOnIt, pairNeighbour]);

        Assert.Equal([isolated, bigHold, footOnIt, pairNeighbour], kept);
    }

    [Fact]
    public void Tangles_are_judged_on_the_stored_circle_footprint_not_the_raw_rectangle()
    {
        // Thin boxes whose rectangles do not overlap at all but whose max-side squares (the stored circles) do.
        var boxes = Enumerable.Range(0, 3).Select(i => new PixelBox(1000 + (i * 40), 1000, 1030 + (i * 40), 1150, 0.9, "hold")).ToList();

        Assert.Empty(TileBoxMerger.DropTangles(boxes));
    }

    [Fact]
    public void Tiny_part_detection_at_the_centre_of_a_hold_is_dropped_but_a_foot_at_its_rim_and_holds_on_a_volume_stay()
    {
        var hold = new PixelBox(2318, 1322, 2402, 1406, 0.9, "hold");       // 84 px
        var boltHole = new PixelBox(2351, 1346, 2368, 1363, 0.6, "hold");   // 17 px, 10 px off-centre
        var footAtRim = new PixelBox(2385, 1390, 2402, 1406, 0.7, "hold");  // small, but at the rim
        var volume = new PixelBox(3000, 1000, 3400, 1400, 0.9, "volume");
        var holdOnVolume = new PixelBox(3180, 1180, 3220, 1220, 0.9, "hold");

        var kept = TileBoxMerger.DropNested([hold, boltHole, footAtRim, volume, holdOnVolume]);

        Assert.Equal([hold, footAtRim, volume, holdOnVolume], kept);
    }
}
