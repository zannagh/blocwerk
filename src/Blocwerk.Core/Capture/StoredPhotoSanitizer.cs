using System.Buffers.Binary;

namespace Blocwerk.Core.Capture;

/// <summary>What <see cref="StoredPhotoSanitizer.Inspect"/> found in one stored photo.</summary>
/// <param name="HasLocation">The photo's EXIF carries a GPS block.</param>
/// <param name="NeedsCleaning">Sanitizing would remove something (GPS, other EXIF, XMP, comments, trailers…).</param>
public readonly record struct PhotoMetadataFacts(bool HasLocation, bool NeedsCleaning);

/// <summary>
/// Removes location and every other piece of metadata from a wall, panel or gallery photo before it is
/// stored, by way of <see cref="ImageMetadataStripper"/> (pixel data copied byte for byte), with two
/// differences from a capture photo:
/// <list type="bullet">
/// <item>The EXIF Orientation survives. Holds are stored in the RAW pixel grid while browsers DISPLAY
/// the photo rotated by that tag, so dropping it would turn the photo underneath its holds. When the
/// source declared an orientation other than 1, a minimal EXIF block holding only that tag is written
/// back.</item>
/// <item>The colour profile survives, so a Display-P3 phone photo keeps its colours.</item>
/// </list>
/// WebP (gallery uploads) is handled the same way: EXIF and XMP chunks go, ICCP stays, and an
/// orientation other than 1 comes back as a minimal EXIF chunk. Other formats (the tests' placeholder
/// bytes) are returned unchanged, exactly as they were stored before.
/// </summary>
public static class StoredPhotoSanitizer
{
    private const ushort TagOrientation = 0x0112;
    private const ushort TagGpsIfd = 0x8825;

    /// <summary>
    /// The bytes to store. Throws <see cref="InvalidOperationException"/> with a readable message when a
    /// JPEG, PNG or WebP is structurally broken; the same input always gives the same output, and sanitizing a
    /// sanitized photo changes nothing.
    /// </summary>
    public static byte[] Sanitize(byte[] image)
    {
        if (!ImageMetadataStripper.CanStrip(image))
        {
            return image;
        }

        var orientation = ReadExif(image).Orientation;
        byte[] stripped;
        try
        {
            stripped = ImageMetadataStripper.Strip(image, keepColour: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("The photo is damaged or incomplete and cannot be used.");
        }

        if (orientation is < 2 or > 8)
        {
            return stripped;
        }

        return CapturePhotoFormat.Sniff(stripped) switch
        {
            CapturePhotoKind.Jpeg => InsertJpegOrientation(stripped, orientation),
            CapturePhotoKind.Png => InsertPngOrientation(stripped, orientation),
            _ => WebpMetadataStripper.AppendExif(stripped, OrientationTiff(orientation)),
        };
    }

    /// <summary>
    /// Whether the photo carries a GPS block, and whether sanitizing would change it. Never throws: a
    /// damaged photo or another format reports nothing to clean. A photo whose only metadata is an
    /// equivalent orientation block is reported clean — sanitizing it could only rewrite that block.
    /// </summary>
    public static PhotoMetadataFacts Inspect(byte[] image)
    {
        if (!ImageMetadataStripper.CanStrip(image))
        {
            return default;
        }

        try
        {
            return new PhotoMetadataFacts(ReadExif(image).HasGps, Sanitize(image).Length != image.Length);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>IFD0's orientation (0 when absent) and whether it points at a GPS IFD. Never throws.</summary>
    private static (ushort Orientation, bool HasGps) ReadExif(byte[] image)
    {
        try
        {
            var tiff = ExifCameraReader.FindTiff(image);
            if (tiff.Length < 8 || tiff[0] != tiff[1] || tiff[0] is not ((byte)'I' or (byte)'M'))
            {
                return (0, false);
            }

            var reader = new ExifTiffReader(tiff.ToArray(), tiff[0] == 'I');
            var ifd0 = reader.ReadIfd(reader.U32(4));
            var orientation = reader.Short(ifd0, TagOrientation) is { } o and >= 0 and <= ushort.MaxValue ? (ushort)o : (ushort)0;
            return (orientation, ifd0.ContainsKey(TagGpsIfd));
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            return (0, false);
        }
    }

    /// <summary>A big-endian TIFF with one IFD holding one entry: Orientation (SHORT).</summary>
    private static byte[] OrientationTiff(ushort orientation)
    {
        var tiff = new byte[26];
        "MM"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10), TagOrientation);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(18), orientation);
        return tiff; // bytes 20..25: value padding and a zero next-IFD offset
    }

    /// <summary>An APP1 "Exif" segment right after SOI, or after the JFIF APP0 when there is one (which must lead).</summary>
    private static byte[] InsertJpegOrientation(byte[] jpeg, ushort orientation)
    {
        var at = 2;
        if (jpeg.Length > 11 && jpeg[2] == 0xFF && jpeg[3] == 0xE0 && jpeg.AsSpan(6).StartsWith("JFIF\0"u8))
        {
            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(4));
        }

        byte[] payload = [.. "Exif\0\0"u8, .. OrientationTiff(orientation)];
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return [.. jpeg.AsSpan(0, at), .. segment, .. jpeg.AsSpan(at)];
    }

    /// <summary>An eXIf chunk right after IHDR (the stripper keeps IHDR first, and eXIf must precede IDAT).</summary>
    private static byte[] InsertPngOrientation(byte[] png, ushort orientation)
    {
        var at = 8 + 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8));
        var tiff = OrientationTiff(orientation);
        var chunk = new byte[12 + tiff.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)tiff.Length);
        "eXIf"u8.CopyTo(chunk.AsSpan(4));
        tiff.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + tiff.Length), Crc32(chunk.AsSpan(4, 4 + tiff.Length)));
        return [.. png.AsSpan(0, at), .. chunk, .. png.AsSpan(at)];
    }

    /// <summary>The PNG chunk CRC (ISO-HDLC CRC-32) over type and data; the input is 30 bytes, so bitwise is fine.</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
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
