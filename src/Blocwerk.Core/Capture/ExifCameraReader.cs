using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Core.Capture;

/// <summary>The camera facts a solve needs from a photo's EXIF.</summary>
public sealed record ExifCameraInfo(string? Make, string? Model, string? LensModel, double? FocalLengthMm, double? Focal35mm)
{
    public static ExifCameraInfo Empty { get; } = new(null, null, null, null, null);

    /// <summary>
    /// A key shared by photos with the same intrinsics: body + lens + raw resolution. Photos in one
    /// group are solved with one camera model, so the resolution must be part of it. Only a hash of
    /// that text is stored and sent to the worker — grouping needs equality, not the device's name.
    /// </summary>
    public string CameraGroup(int width, int height)
    {
        var body = string.Join(' ', new[] { Make, Model }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var lens = LensModel ?? (FocalLengthMm is { } f ? $"f={f:0.###}mm" : Focal35mm is { } e ? $"f35={e:0.#}" : "unknown lens");
        var group = $"{(body.Length > 0 ? body : "unknown camera")}|{lens}|{width}x{height}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(group));
        return "cam-" + Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }
}

/// <summary>
/// A small, dependency-free EXIF reader (JPEG APP1 and PNG eXIf): Make, Model, LensModel,
/// FocalLength and FocalLengthIn35mmFilm. Never throws — a missing or malformed block is "no EXIF".
/// </summary>
public static class ExifCameraReader
{
    private const ushort TagMake = 0x010F;
    private const ushort TagModel = 0x0110;
    private const ushort TagExifIfd = 0x8769;
    private const ushort TagFocalLength = 0x920A;
    private const ushort TagFocal35 = 0xA405;
    private const ushort TagLensModel = 0xA434;

    public static ExifCameraInfo Read(byte[] image)
    {
        try
        {
            var tiff = FindTiff(image);
            return tiff.IsEmpty ? ExifCameraInfo.Empty : ParseTiff(tiff);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException
                                       or InvalidDataException)
        {
            return ExifCameraInfo.Empty;
        }
    }

    internal static ReadOnlySpan<byte> FindTiff(byte[] image) => CapturePhotoFormat.Sniff(image) switch
    {
        CapturePhotoKind.Jpeg => FindJpegTiff(image),
        CapturePhotoKind.Png => FindPngTiff(image),
        _ when WebpMetadataStripper.IsWebp(image) => WebpMetadataStripper.FindExif(image),
        _ => [],
    };

    /// <summary>The first APP1 "Exif" payload of the primary image, from the bounds-checked segment walk.</summary>
    private static ReadOnlySpan<byte> FindJpegTiff(byte[] src)
    {
        foreach (var piece in JpegStructure.Parse(src))
        {
            if (piece.Marker == 0xDA)
            {
                break;
            }

            var payload = src.AsSpan(piece.Start + 4, Math.Max(0, piece.Length - 4));
            if (piece.Marker == 0xE1 && payload.StartsWith("Exif\0\0"u8))
            {
                return payload[6..];
            }
        }

        return [];
    }

    /// <summary>The eXIf chunk. Lengths are unsigned and bounded, so every step moves forward.</summary>
    private static ReadOnlySpan<byte> FindPngTiff(byte[] src)
    {
        var pos = 8;
        while (pos <= src.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(pos, 4));
            if (length > (uint)(src.Length - pos - 12))
            {
                return [];
            }

            if (src.AsSpan(pos + 4, 4).SequenceEqual("eXIf"u8))
            {
                return src.AsSpan(pos + 8, (int)length);
            }

            pos += 12 + (int)length;
        }

        return [];
    }

    private static ExifCameraInfo ParseTiff(ReadOnlySpan<byte> tiff)
    {
        var little = tiff[0] == 'I';
        var reader = new ExifTiffReader(tiff.ToArray(), little);
        var ifd0 = reader.ReadIfd(reader.U32(4));
        string? make = reader.Ascii(ifd0, TagMake), model = reader.Ascii(ifd0, TagModel), lens = null;
        double? focal = null, focal35 = null;
        if (ifd0.TryGetValue(TagExifIfd, out var exifEntry))
        {
            var exif = reader.ReadIfd(reader.U32(exifEntry.ValueOffset));
            lens = reader.Ascii(exif, TagLensModel);
            focal = reader.Rational(exif, TagFocalLength);
            focal35 = reader.Short(exif, TagFocal35);
        }

        return new ExifCameraInfo(make, model, lens, focal, focal35 is > 0 ? focal35 : null);
    }
}
