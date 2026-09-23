using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace Blocwerk.HoldDetection.Tests.Tiling;

public class TilingSettingsTests
{
    [Fact]
    public void Defaults_turn_tiling_on_with_the_measured_parameters()
    {
        var settings = HoldDetectionSettings.Bind(Section([])).Tiling;

        Assert.True(settings.Enabled);
        Assert.Equal(1280, settings.TileSize);
        Assert.Equal(256, settings.Overlap);
        Assert.Equal(0.35, settings.Confidence);
        Assert.Equal("nearest", settings.Sampling);
    }

    [Fact]
    public void Config_keys_bind_and_false_switches_tiling_off()
    {
        var settings = HoldDetectionSettings.Bind(Section(new()
        {
            ["HoldDetection:Tiling:Enabled"] = "false",
            ["HoldDetection:Tiling:TileSize"] = "960",
            ["HoldDetection:Tiling:Overlap"] = "192",
            ["HoldDetection:Tiling:Confidence"] = "0.5",
            ["HoldDetection:Tiling:Sampling"] = "Linear",
        })).Tiling;

        Assert.False(settings.Enabled);
        Assert.Equal(960, settings.TileSize);
        Assert.Equal(192, settings.Overlap);
        Assert.Equal(0.5, settings.Confidence);
        Assert.Equal("linear", settings.Sampling);
    }

    [Theory]
    [InlineData("nonsense", "abc", "x", "7")]
    [InlineData("", "10", "-3", "0")]
    public void Unparseable_or_out_of_range_values_fall_back_to_defaults(string enabled, string tile, string overlap, string conf)
    {
        var settings = HoldDetectionSettings.Bind(Section(new()
        {
            ["HoldDetection:Tiling:Enabled"] = enabled,
            ["HoldDetection:Tiling:TileSize"] = tile,
            ["HoldDetection:Tiling:Overlap"] = overlap,
            ["HoldDetection:Tiling:Confidence"] = conf,
        })).Tiling;

        Assert.True(settings.Enabled);
        Assert.Equal(1280, settings.TileSize);
        Assert.Equal(256, settings.Overlap);
        Assert.Equal(0.35, settings.Confidence);
    }

    [Fact]
    public void Overlap_too_large_for_the_tile_is_clamped_so_the_grid_advances()
    {
        var settings = HoldDetectionSettings.Bind(Section(new()
        {
            ["HoldDetection:Tiling:TileSize"] = "640",
            ["HoldDetection:Tiling:Overlap"] = "640",
        })).Tiling;

        Assert.Equal(160, settings.Overlap);
    }

    private static IConfiguration Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
