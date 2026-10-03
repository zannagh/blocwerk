using Blocwerk.Core.Configuration;
using Blocwerk.HoldDetection.Tiling;

namespace Blocwerk.HoldDetection.Tests.Tiling;

/// <summary>The tiled path keeps the whole-image output contract; runs the process's single YOLO.</summary>
[Collection(OnnxRuntimeCollection.Name)]
public class TiledDetectionContractTests(OnnxRuntimeFixture onnx)
{
    private static readonly string WallPath = Path.Combine(AppContext.BaseDirectory, "walls", "Test-Wall.jpeg");

    [Fact]
    public void Radius_is_the_long_box_side_over_twice_the_long_image_side_and_volumes_are_white()
    {
        var boxes = new List<PixelBox>
        {
            new(100, 200, 160, 240, 0.9, "hold"),
            new(1000, 1000, 1300, 1200, 0.8, "volume"),
        };

        var (holds, outOfBounds, _) = YoloHoldDetectionService.ToHolds(boxes, 4000, 3000);

        Assert.Equal(0, outOfBounds);
        Assert.Equal(new(0.0325, 0.0733, 0.0075, null, 0.9), holds[0]);
        Assert.Equal(new(0.2875, 0.3667, 0.0375, "white", 0.8), holds[1]);
    }

    [SkippableFact]
    public async Task Tiled_and_whole_image_paths_honour_the_switch_and_the_same_contract()
    {
        Skip.If(onnx.Yolo is null || onnx.Options is null, "Model not found at models/climbingcrux.onnx");
        Skip.If(!File.Exists(WallPath), "Test-Wall.jpeg not found");
        var bytes = await File.ReadAllBytesAsync(WallPath);

        var whole = await Detect(bytes, new HoldDetectionTilingSettings { Enabled = false });
        var tiled = await Detect(bytes, new HoldDetectionTilingSettings { TileSize = 1024, Overlap = 256 });

        Assert.NotEmpty(whole);

        // Strictly more: a silent fallback to colour detection would return the same set twice.
        Assert.True(tiled.Count > whole.Count, $"tiled {tiled.Count} <= whole {whole.Count}");
        Assert.All(whole.Concat(tiled), h =>
        {
            Assert.InRange(h.X, 0, 1);
            Assert.InRange(h.Y, 0, 1);
            Assert.InRange(h.Radius, 0, 0.5);
            Assert.True(h.Color is null or "white");
        });
    }

    private async Task<List<Blocwerk.Core.Abstractions.DetectedHold>> Detect(byte[] bytes, HoldDetectionTilingSettings settings)
    {
        using var service = new YoloHoldDetectionService(onnx.Yolo!, onnx.Options!, settings);
        return await service.DetectHoldsAsync(bytes);
    }
}
