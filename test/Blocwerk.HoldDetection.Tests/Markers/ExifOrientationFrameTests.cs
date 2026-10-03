using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;
using Blocwerk.HoldDetection.Markers;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Markers;

/// <summary>
/// The hold detector (SkiaSharp) ignores EXIF orientation, so hold X/Y live in the RAW pixel grid.
/// OpenCV 4.x applies EXIF orientation on decode by default; the marker and outline decoders must opt
/// out, or a phone photo tagged "rotate 90°" puts markers/outlines in a different frame than the holds.
/// </summary>
public class ExifOrientationFrameTests
{
    private static readonly string Fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Glyphs", "img2783-topleft.jpg");

    [Fact]
    public void Fixture_WithInjectedOrientation6_IsReadAsRotatedByDefaultOpenCv()
    {
        var raw = File.ReadAllBytes(Fixture);
        var rotated = WithExifOrientation(raw, 6);

        Assert.Equal(ExifOrientation.Normal, ExifOrientation.Read(raw));
        Assert.Equal(6, ExifOrientation.Read(rotated));

        // Guard that the test image really exercises the trap: plain ImDecode swaps the axes.
        using var plain = Cv2.ImDecode(raw, ImreadModes.Color);
        using var applied = Cv2.ImDecode(rotated, ImreadModes.Color);
        Assert.Equal(plain.Width, applied.Height);
        Assert.Equal(plain.Height, applied.Width);
    }

    [Fact]
    public async Task MarkerCorners_OnOrientation6Photo_MatchTheRawFrame()
    {
        var raw = File.ReadAllBytes(Fixture);
        var rotated = WithExifOrientation(raw, 6);
        var service = new ArucoMarkerDetectionService();

        var expected = await service.DetectAsync(raw, null, CancellationToken.None);
        var actual = await service.DetectAsync(rotated, null, CancellationToken.None);

        Assert.Equal(expected.ImageWidth, actual.ImageWidth);
        Assert.Equal(expected.ImageHeight, actual.ImageHeight);
        Assert.Equal(new[] { 0, 5, 12 }, actual.Markers.Select(m => m.Id).ToArray());
        for (var i = 0; i < expected.Markers.Count; i++)
        {
            for (var c = 0; c < 4; c++)
            {
                Assert.Equal(expected.Markers[i].CornersPx[c].X, actual.Markers[i].CornersPx[c].X, 3);
                Assert.Equal(expected.Markers[i].CornersPx[c].Y, actual.Markers[i].CornersPx[c].Y, 3);
            }
        }
    }

    [Fact]
    public void Outline_OnOrientation6Photo_MatchesTheRawFrame()
    {
        var raw = File.ReadAllBytes(Fixture);
        var rotated = WithExifOrientation(raw, 6);
        var service = new OpenCvHoldOutlineService();
        var seed = new HoldSeed(0.5, 0.5, 0.04);

        using var rawSession = service.OpenSession(raw);
        using var rotatedSession = service.OpenSession(rotated);

        Assert.Equal(rawSession.ImageWidth, rotatedSession.ImageWidth);
        Assert.Equal(rawSession.ImageHeight, rotatedSession.ImageHeight);
        var expected = rawSession.Outline(seed);
        var actual = rotatedSession.Outline(seed);
        Assert.Equal(expected.Method, actual.Method);
        Assert.Equal(expected.Polygon, actual.Polygon);
    }

    /// <summary>Inserts a minimal big-endian EXIF APP1 segment carrying only the orientation tag after SOI.</summary>
    internal static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] payload =
        [
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0,
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, // TIFF header, IFD0 at offset 8
            0, 1, // one entry
            0x01, 0x12, 0, 3, 0, 0, 0, 1, (byte)(orientation >> 8), (byte)orientation, 0, 0, // Orientation, SHORT, 1
            0, 0, 0, 0, // no next IFD
        ];
        var length = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }
}
