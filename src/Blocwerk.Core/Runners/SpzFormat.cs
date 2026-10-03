// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Structural check of an <c>.spz</c> (Niantic's gzip-compressed splat format, versions 2 and 3): the header is read,
/// then the whole body is decompressed in a stream (never held in memory, and never more than the header's own size
/// plus one byte, so a gzip bomb stops right there) and must be exactly as long as the header says.
/// </summary>
public static class SpzFormat
{
    private const uint Magic = 0x5053474E; // "NGSP"
    private const int HeaderBytes = 16;

    /// <summary>Throws <see cref="InvalidDataException"/> unless <paramref name="gzip"/> is a well-formed .spz.</summary>
    public static void Validate(Stream gzip, long maxSplats)
    {
        using var body = new GZipStream(gzip, CompressionMode.Decompress, leaveOpen: true);
        var header = new byte[HeaderBytes];
        body.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var shDegree = header[12];
        if (magic != Magic || version is < 2 or > 3 || count == 0 || count > maxSplats || shDegree > 3)
        {
            throw new InvalidDataException("The upload is not a valid .spz splat.");
        }

        var expected = checked(count * BytesPerPoint(version, shDegree));
        var actual = CountCapped(body, expected + 1);
        if (actual != expected)
        {
            throw new InvalidDataException(actual < expected ? "The .spz is truncated." : "The .spz has data past its splats.");
        }
    }

    /// <summary>Positions (3 × 24-bit), alpha, colour, scales, rotation (v2: 3 bytes, v3: 4) and the SH coefficients.</summary>
    internal static long BytesPerPoint(uint version, int shDegree)
    {
        var shCoefficients = shDegree switch
        {
            0 => 0,
            1 => 3,
            2 => 8,
            _ => 15,
        };
        return 9 + 1 + 3 + 3 + (version >= 3 ? 4 : 3) + (shCoefficients * 3);
    }

    private static long CountCapped(Stream body, long cap)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while (total < cap && (read = body.Read(buffer, 0, (int)Math.Min(buffer.Length, cap - total))) > 0)
        {
            total += read;
        }

        return total;
    }
}
