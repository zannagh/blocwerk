using System.Buffers.Binary;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Removes ALL metadata (EXIF incl. GPS and orientation, XMP, ICC, IPTC, MPF, comments, text chunks)
/// and everything appended after the primary image (JPEG: past its EOI; PNG: past IEND; WebP: past the
/// RIFF payload) from a JPEG, PNG or WebP (<see cref="WebpMetadataStripper"/>) without re-encoding: the compressed pixel data is copied byte for byte, so marker
/// corners detected on the original stay valid. Dropping the orientation tag is deliberate — the
/// markers were detected on the RAW pixel grid, and a decoder that honours EXIF (the compute
/// service's) would otherwise rotate the image away from the solved camera.
/// </summary>
public static class ImageMetadataStripper
{
    /// <summary>Returns the stripped bytes; throws <see cref="InvalidDataException"/> for other formats or corrupt files.</summary>
    public static byte[] Strip(byte[] image) => Strip(image, keepColour: false);

    /// <summary>
    /// As <see cref="Strip(byte[])"/>; with <paramref name="keepColour"/> the colour-rendering segments
    /// survive too (JPEG: the ICC profile and the Adobe colour-transform marker; PNG: iCCP, sRGB, gAMA,
    /// cHRM). They carry no location or device identity, and dropping them visibly shifts the colours of
    /// a Display-P3 phone photo — which matters for a photo people look at, not for a capture photo.
    /// </summary>
    public static byte[] Strip(byte[] image, bool keepColour) => CapturePhotoFormat.Sniff(image) switch
    {
        CapturePhotoKind.Jpeg => StripJpeg(image, keepColour),
        CapturePhotoKind.Png => StripPng(image, keepColour),
        _ when WebpMetadataStripper.IsWebp(image) => WebpMetadataStripper.Strip(image, keepColour),
        _ => throw new InvalidDataException("Only JPEG, PNG and WebP photos can be processed."),
    };

    /// <summary>Whether <see cref="Strip(byte[], bool)"/> handles this format (JPEG, PNG or WebP).</summary>
    public static bool CanStrip(ReadOnlySpan<byte> image) =>
        CapturePhotoFormat.Sniff(image) is CapturePhotoKind.Jpeg or CapturePhotoKind.Png || WebpMetadataStripper.IsWebp(image);

    /// <summary>
    /// Rebuilds the PRIMARY image only: SOI, the kept segments, every scan's entropy-coded data byte
    /// for byte, EOI. Whatever follows the primary image's EOI (MPF gain/depth maps with their own
    /// EXIF/GPS, Motion Photo videos, vendor trailers) is dropped.
    /// </summary>
    private static byte[] StripJpeg(byte[] src, bool keepColour)
    {
        var pieces = JpegStructure.Parse(src);
        using var output = new MemoryStream(src.Length);
        output.Write(src, 0, 2); // SOI
        var sawScan = false;
        foreach (var piece in pieces)
        {
            sawScan |= piece.IsEntropyData;
            if (piece.IsEntropyData || piece.Length == 2
                || KeepJpegSegment(piece.Marker, src.AsSpan(piece.Start + 4, piece.Length - 4), keepColour))
            {
                output.Write(src, piece.Start, piece.Length);
            }
        }

        if (!sawScan)
        {
            throw new InvalidDataException("The JPEG is truncated (no image data).");
        }

        output.Write([0xFF, 0xD9]); // EOI
        return output.ToArray();
    }

    /// <summary>APP0 only when it is the plain JFIF header (plus, with keepColour, the ICC profile and Adobe marker); every other APPn (EXIF, XMP, IPTC…) and COM go.</summary>
    private static bool KeepJpegSegment(byte marker, ReadOnlySpan<byte> payload, bool keepColour)
    {
        if (marker == 0xE0)
        {
            return payload.StartsWith("JFIF\0"u8);
        }

        if (keepColour && marker == 0xE2)
        {
            return payload.StartsWith("ICC_PROFILE\0"u8);
        }

        if (keepColour && marker == 0xEE)
        {
            return payload.StartsWith("Adobe"u8);
        }

        return marker is not ((>= 0xE1 and <= 0xEF) or 0xFE);
    }

    private static byte[] StripPng(byte[] src, bool keepColour)
    {
        using var output = new MemoryStream(src.Length);
        output.Write(src, 0, 8);
        var pos = 8;
        while (pos + 12 <= src.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(src.AsSpan(pos, 4));
            if (length > int.MaxValue || pos + 12 + (long)length > src.Length)
            {
                throw new InvalidDataException("The PNG is corrupt (chunk length out of range).");
            }

            var type = System.Text.Encoding.ASCII.GetString(src, pos + 4, 4);
            var total = 12 + (int)length;
            if (type is "IHDR" or "PLTE" or "tRNS" or "IDAT" or "IEND"
                || (keepColour && type is "iCCP" or "sRGB" or "gAMA" or "cHRM"))
            {
                output.Write(src, pos, total);
            }

            pos += total;
            if (type == "IEND")
            {
                return output.ToArray();
            }
        }

        throw new InvalidDataException("The PNG is truncated (no IEND chunk).");
    }
}
