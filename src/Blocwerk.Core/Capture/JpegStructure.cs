using System.Buffers.Binary;

namespace Blocwerk.Core.Capture;

/// <summary>One byte range of a JPEG's primary image: a marker segment, or entropy-coded scan data.</summary>
/// <param name="Marker">The segment's marker byte (0xD8…0xFE), or 0 for entropy-coded data.</param>
/// <param name="Start">Offset of the range (the 0xFF of a marker).</param>
/// <param name="Length">Length of the whole range, marker and length field included.</param>
internal readonly record struct JpegPiece(byte Marker, int Start, int Length)
{
    public bool IsEntropyData => Marker == 0;
}

/// <summary>
/// Walks a JPEG's PRIMARY image from SOI to its EOI: every marker segment, and the entropy-coded data
/// of every scan (a progressive JPEG has many), honouring 0xFF00 stuffing and RSTn markers. Everything
/// after the primary image's EOI — Multi-Picture-Format gain/depth maps, Motion Photo videos, vendor
/// trailers — is not part of the result. Every loop advances by at least one byte, and every length is
/// bounds-checked, so a hostile file ends in <see cref="InvalidDataException"/>, never in a spin.
/// </summary>
internal static class JpegStructure
{
    private const byte Sos = 0xDA;
    private const byte Eoi = 0xD9;

    /// <summary>The pieces between SOI and EOI (both excluded), in file order.</summary>
    public static List<JpegPiece> Parse(ReadOnlySpan<byte> src)
    {
        if (src.Length < 4 || src[0] != 0xFF || src[1] != 0xD8)
        {
            throw new InvalidDataException("The JPEG is corrupt (no start-of-image marker).");
        }

        var pieces = new List<JpegPiece>();
        var pos = 2;
        while (true)
        {
            pos = SkipFill(src, pos);
            var marker = src[pos + 1];
            if (marker == Eoi)
            {
                return pieces;
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD7))
            {
                pieces.Add(new JpegPiece(marker, pos, 2));
                pos += 2;
                continue;
            }

            var length = Segment(src, pos, marker);
            pieces.Add(new JpegPiece(marker, pos, 2 + length));
            pos += 2 + length;
            if (marker == Sos)
            {
                var end = EntropyEnd(src, pos);
                pieces.Add(new JpegPiece(0, pos, end - pos));
                pos = end;
            }
        }
    }

    /// <summary>Skips 0xFF fill bytes; returns the offset of the 0xFF that precedes the marker byte.</summary>
    private static int SkipFill(ReadOnlySpan<byte> src, int pos)
    {
        if (pos >= src.Length || src[pos] != 0xFF)
        {
            throw new InvalidDataException("The JPEG is corrupt or truncated (segment marker expected).");
        }

        while (pos + 1 < src.Length && src[pos + 1] == 0xFF)
        {
            pos++;
        }

        if (pos + 1 >= src.Length)
        {
            throw new InvalidDataException("The JPEG is truncated (no end-of-image marker).");
        }

        if (src[pos + 1] is 0x00 or 0xD8)
        {
            throw new InvalidDataException("The JPEG is corrupt (invalid marker).");
        }

        return pos;
    }

    /// <summary>The payload length (length field included) of the segment at <paramref name="pos"/>.</summary>
    private static int Segment(ReadOnlySpan<byte> src, int pos, byte marker)
    {
        if (pos + 4 > src.Length)
        {
            throw new InvalidDataException("The JPEG is truncated (segment header cut off).");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(pos + 2, 2));
        if (length < 2 || length > src.Length - pos - 2)
        {
            throw new InvalidDataException($"The JPEG is corrupt (segment 0x{marker:X2} length out of range).");
        }

        return length;
    }

    /// <summary>
    /// The end of a scan's entropy-coded data: the offset of the first 0xFF that starts a real marker
    /// (not a 0xFF00 stuffed byte, not an RSTn — both belong to the data).
    /// </summary>
    private static int EntropyEnd(ReadOnlySpan<byte> src, int pos)
    {
        while (true)
        {
            var next = src[pos..].IndexOf((byte)0xFF);
            if (next < 0 || pos + next + 1 >= src.Length)
            {
                throw new InvalidDataException("The JPEG is truncated (image data has no end).");
            }

            pos += next;
            var follower = src[pos + 1];
            if (follower == 0x00 || follower is >= 0xD0 and <= 0xD7)
            {
                pos += 2;
                continue;
            }

            return pos;
        }
    }
}
