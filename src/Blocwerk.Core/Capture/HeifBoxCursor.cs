// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>An ISO-BMFF box: where its payload starts and where the box ends (absolute offsets).</summary>
/// <param name="Payload">The first byte after the box header.</param>
/// <param name="End">The first byte after the box.</param>
internal readonly record struct HeifBox(int Payload, int End);

/// <summary>A forward reader of big-endian unsigned fields of 0–8 bytes (the <c>iloc</c> box's variable-width fields).</summary>
/// <param name="data">The file.</param>
/// <param name="position">Where reading starts.</param>
internal sealed class HeifBoxCursor(byte[] data, int position)
{
    /// <summary>Reads <paramref name="bytes"/> bytes as a big-endian unsigned number (0 bytes → 0).</summary>
    /// <param name="bytes">The field width (0–8; the spec allows 0, 4 and 8, sometimes 1 or 2).</param>
    /// <returns>The value.</returns>
    public long Read(int bytes)
    {
        if (bytes is < 0 or > 8 || position + bytes > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "The HEIF item location table is malformed.");
        }

        var value = 0UL;
        for (var i = 0; i < bytes; i++)
        {
            value = (value << 8) | data[position++];
        }

        return checked((long)value);
    }

    /// <summary>Skips <paramref name="bytes"/> bytes.</summary>
    /// <param name="bytes">How many.</param>
    public void Skip(int bytes)
    {
        if (bytes < 0 || position + bytes > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "The HEIF item location table is malformed.");
        }

        position += bytes;
    }
}
