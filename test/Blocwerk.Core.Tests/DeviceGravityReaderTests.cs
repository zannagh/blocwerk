// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The iPhone gravity vector (Apple maker note 0x0008) from synthetic EXIF: JPEG and HEIC containers, both byte orders,
/// a portrait and a landscape shot, and everything that must give null; the stored photo never keeps the maker note.
/// </summary>
public class DeviceGravityReaderTests
{
    // Values shaped like Phase 0's: a portrait shot (aY ≈ −1) and a landscape shot (aX ≈ −1).
    private static readonly (double X, double Y, double Z) Portrait = (-0.03822121769, -0.9868407256, -0.1132549419);
    private static readonly (double X, double Y, double Z) Landscape = (-0.9912345678, 0.0212345678, -0.1234567891);

    public static TheoryData<bool, bool> Orientations => new() { { true, false }, { false, true } };

    [Theory]
    [MemberData(nameof(Orientations))]
    public void Jpeg_BothOrientations_BothByteOrders(bool portrait, bool little)
    {
        var v = portrait ? Portrait : Landscape;
        var jpeg = AppleMakerNoteExif.Jpeg(CaptureScenario.TinyJpeg(), AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(v), little));

        AssertVector(v, DeviceGravityReader.Read(jpeg));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Heic_ExifItemInMdatOrIdat(bool inIdat)
    {
        var heic = AppleMakerNoteExif.Heic(AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(Landscape)), inIdat);

        Assert.Equal(CapturePhotoKind.Heic, CapturePhotoFormat.Sniff(heic));
        AssertVector(Landscape, DeviceGravityReader.Read(heic));
    }

    [Fact]
    public void NoMakerNote_OtherMakerNote_NoAcceleration_OrNoExif_GiveNull()
    {
        var tiny = CaptureScenario.TinyJpeg();

        Assert.Null(DeviceGravityReader.Read(AppleMakerNoteExif.Jpeg(tiny, AppleMakerNoteExif.Tiff(null))));
        Assert.Null(DeviceGravityReader.Read(AppleMakerNoteExif.Jpeg(tiny, AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(Portrait, "Nikon\0\0\0\0\0")))));
        Assert.Null(DeviceGravityReader.Read(AppleMakerNoteExif.Jpeg(tiny, AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(null)))));
        Assert.Null(DeviceGravityReader.Read(tiny));
        Assert.Null(DeviceGravityReader.Read(ExifJpeg.Build(tiny)));
        Assert.Null(DeviceGravityReader.Read(AppleMakerNoteExif.Heic(AppleMakerNoteExif.Tiff(null))));
    }

    [Fact]
    public void TruncatedOrGarbage_NeverThrows()
    {
        var full = AppleMakerNoteExif.Jpeg(CaptureScenario.TinyJpeg(), AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(Portrait)));
        var heic = AppleMakerNoteExif.Heic(AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(Portrait)));
        for (var cut = 0; cut < heic.Length; cut += 7)
        {
            _ = DeviceGravityReader.Read(heic[..cut]);
        }

        for (var cut = 0; cut < 400 && cut < full.Length; cut += 5)
        {
            _ = DeviceGravityReader.Read(full[..cut]);
        }

        var note = AppleMakerNoteExif.MakerNote(Portrait);
        note[^1] = 0; // a zero denominator
        note[^2] = 0;
        note[^3] = 0;
        note[^4] = 0;
        Assert.Null(DeviceGravityReader.FromAppleMakerNote(note));
        Assert.Null(DeviceGravityReader.Read([0, 0, 0, 24, .. "ftypheic"u8.ToArray(), .. new byte[64]]));
    }

    [Fact]
    public void Strippers_DropTheMakerNote()
    {
        var jpeg = AppleMakerNoteExif.Jpeg(CaptureScenario.TinyJpeg(), AppleMakerNoteExif.Tiff(AppleMakerNoteExif.MakerNote(Portrait)));
        Assert.NotEqual(-1, jpeg.AsSpan().IndexOf("Apple iOS"u8));

        foreach (var stored in new[] { ImageMetadataStripper.Strip(jpeg), StoredPhotoSanitizer.Sanitize(jpeg) })
        {
            Assert.Equal(-1, stored.AsSpan().IndexOf("Apple iOS"u8));
            Assert.Equal(-1, stored.AsSpan().IndexOf("Exif\0\0"u8));
            Assert.Null(DeviceGravityReader.Read(stored));
        }
    }

    [Fact]
    public void Request_CarriesTheVectorAndTheStoredSize()
    {
        var photos = new[]
        {
            new CaptureSfmPhoto(1, 3024, 4032, new DeviceGravity(Portrait.X, Portrait.Y, Portrait.Z)),
            new CaptureSfmPhoto(2, 4032, 3024, null),
        };

        var request = JsonNode.Parse(CaptureSfmDocuments.BuildRequest(
            photos, new Dictionary<int, IReadOnlyList<double[]>>(), [], null, new Dictionary<string, string>(), null, 125))!;

        var first = request["photos"]![0]!;
        Assert.Equal([Portrait.X, Portrait.Y, Portrait.Z], first["deviceGravity"]!.AsArray().Select(n => n!.GetValue<double>()));
        Assert.Equal([3024, 4032], first["imageSize"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Null(request["photos"]![1]!["deviceGravity"]);
        Assert.Equal([4032, 3024], request["photos"]![1]!["imageSize"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    private static void AssertVector((double X, double Y, double Z) expected, DeviceGravity? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(AppleMakerNoteExif.Written(expected.X), actual.X, 12);
        Assert.Equal(AppleMakerNoteExif.Written(expected.Y), actual.Y, 12);
        Assert.Equal(AppleMakerNoteExif.Written(expected.Z), actual.Z, 12);
        Assert.Equal(expected.X, actual.X, 7);
    }
}
