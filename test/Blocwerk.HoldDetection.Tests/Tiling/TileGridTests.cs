using Blocwerk.HoldDetection.Tiling;

namespace Blocwerk.HoldDetection.Tests.Tiling;

public class TileGridTests
{
    [Fact]
    public void Phone_photo_gets_a_4x3_grid_pinned_to_the_far_edges()
    {
        var windows = TileGrid.Compute(4032, 3024, 1280, 256);

        Assert.Equal(12, windows.Count);
        Assert.Equal([0, 1024, 2048, 2752], windows.Select(w => w.X).Distinct());
        Assert.Equal([0, 1024, 1744], windows.Select(w => w.Y).Distinct());
        Assert.All(windows, w => Assert.Equal((1280, 1280), (w.Width, w.Height)));
    }

    [Theory]
    [InlineData(4032, 3024, 1280, 256)]
    [InlineData(3955, 2444, 1280, 256)]
    [InlineData(1281, 700, 1280, 256)]
    [InlineData(5000, 5000, 640, 128)]
    public void Grid_covers_every_pixel_and_neighbours_overlap_at_least_the_configured_amount(int w, int h, int tile, int overlap)
    {
        var windows = TileGrid.Compute(w, h, tile, overlap);

        Assert.All(windows, win => Assert.True(win.X >= 0 && win.Y >= 0 && win.Right <= w && win.Bottom <= h));
        AssertAxis(TileGrid.Starts(w, tile, overlap), w, tile, overlap);
        AssertAxis(TileGrid.Starts(h, tile, overlap), h, tile, overlap);
    }

    [Fact]
    public void Image_smaller_than_a_tile_is_one_window_of_its_own_size()
    {
        var windows = TileGrid.Compute(800, 600, 1280, 256);

        Assert.Equal([new TileWindow(0, 0, 800, 600)], windows);
    }

    [Fact]
    public void Overlap_must_be_smaller_than_the_tile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TileGrid.Compute(4000, 3000, 256, 256));
    }

    private static void AssertAxis(List<int> starts, int size, int tile, int overlap)
    {
        Assert.Equal(0, starts[0]);
        Assert.Equal(Math.Max(size - tile, 0), starts[^1]);
        for (int i = 1; i < starts.Count; i++)
        {
            int shared = starts[i - 1] + tile - starts[i];
            Assert.True(shared >= overlap, $"windows at {starts[i - 1]} and {starts[i]} share only {shared} px");
            Assert.True(starts[i] > starts[i - 1]);
        }
    }
}
