using System.Buffers.Binary;
using System.Diagnostics;
using Blocwerk.HoldDetection.Markers;
using Blocwerk.HoldDetection.Outlines;
using SkiaSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>
/// The OpenCV decoders refuse an image whose header claims more than 100 megapixels before
/// <c>Cv2.ImDecode</c> allocates for it (a tiny PNG can claim 20 000 × 20 000).
/// </summary>
public class DecodePixelLimitTests
{
    [Fact]
    public async Task MarkerDetection_RefusesADecompressionBomb_BeforeDecoding()
    {
        var bomb = PngClaiming(20_000, 20_000);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new ArucoMarkerDetectionService().DetectAsync(bomb, null, CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void OutlineSession_RefusesADecompressionBomb_BeforeDecoding()
    {
        Assert.Throws<ArgumentException>(() => new OpenCvHoldOutlineService().OpenSession(PngClaiming(20_000, 20_000)));
    }

    [Fact]
    public async Task NormalSizes_StillDecode()
    {
        var png = PngClaiming(64, 48, keepPixels: true);

        var result = await new ArucoMarkerDetectionService().DetectAsync(png, null, CancellationToken.None);

        Assert.Equal((64, 48), (result.ImageWidth, result.ImageHeight));
    }

    /// <summary>A real 64×48 PNG whose IHDR is rewritten to claim another size (CRC fixed up).</summary>
    private static byte[] PngClaiming(int width, int height, bool keepPixels = false)
    {
        using var bitmap = new SKBitmap(64, 48);
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        var png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        if (keepPixels)
        {
            return png;
        }

        // Signature (8) + length (4) + "IHDR" (4): width and height follow, then the rest of IHDR and its CRC.
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
