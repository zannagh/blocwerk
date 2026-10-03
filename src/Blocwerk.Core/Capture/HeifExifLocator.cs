// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Finds the EXIF (TIFF) block of a HEIC/HEIF file without decoding it (ISO/IEC 23008-12): the top-level <c>meta</c>
/// box → <c>iinf</c> (the item whose type is <c>Exif</c>) → <c>iloc</c> (where its bytes are: file offsets, or inside
/// <c>idat</c>) → the item, whose first 4 bytes are the big-endian offset of the TIFF header behind them. Malformed input
/// gives an empty span or an <see cref="ArgumentOutOfRangeException"/> / <see cref="OverflowException"/> (callers catch).
/// </summary>
internal static class HeifExifLocator
{
    private const int MaxExifBytes = 1 << 22;

    public static ReadOnlySpan<byte> FindTiff(byte[] file)
    {
        if (FindBox(file, 0, file.Length, "meta") is not { } meta)
        {
            return [];
        }

        var children = meta.Payload + 4; // meta is a FullBox
        if (FindBox(file, children, meta.End, "iinf") is not { } iinf
            || FindBox(file, children, meta.End, "iloc") is not { } iloc
            || ExifItemId(file, iinf) is not { } id)
        {
            return [];
        }

        var item = ItemBytes(file, iloc, id, FindBox(file, children, meta.End, "idat"));
        if (item is not { Length: > 4 })
        {
            return [];
        }

        var tiffAt = 4L + BinaryPrimitives.ReadUInt32BigEndian(item);
        return tiffAt < item.Length ? item.AsSpan((int)tiffAt) : [];
    }

    private static HeifBox? FindBox(byte[] file, int from, int end, string type)
    {
        var pos = from;
        while (pos <= end - 8)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(pos, 4));
            var header = 8;
            if (size == 1)
            {
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(pos + 8, 8)));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }

            if (size < header || size > end - pos)
            {
                return null;
            }

            if (file.AsSpan(pos + 4, 4).SequenceEqual(System.Text.Encoding.ASCII.GetBytes(type)))
            {
                return new HeifBox(pos + header, pos + (int)size);
            }

            pos += (int)size;
        }

        return null;
    }

    /// <summary>The id of the first <c>infe</c> (version ≥ 2) whose item type is <c>Exif</c>.</summary>
    private static uint? ExifItemId(byte[] file, HeifBox iinf)
    {
        var version = file[iinf.Payload];
        var pos = iinf.Payload + 4 + (version == 0 ? 2 : 4);
        while (FindBox(file, pos, iinf.End, "infe") is { } infe)
        {
            var v = file[infe.Payload];
            var at = infe.Payload + 4;
            if (v >= 2)
            {
                var id = v == 2 ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at, 2)) : BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at, 4));
                var typeAt = at + (v == 2 ? 2 : 4) + 2;
                if (file.AsSpan(typeAt, 4).SequenceEqual("Exif"u8))
                {
                    return id;
                }
            }

            pos = infe.End;
        }

        return null;
    }

    /// <summary>The item's bytes (its extents concatenated), or null when it is not in the file or too large.</summary>
    private static byte[]? ItemBytes(byte[] file, HeifBox iloc, uint wanted, HeifBox? idat)
    {
        var r = new HeifBoxCursor(file, iloc.Payload);
        var version = r.Read(1);
        r.Skip(3);
        var sizes = (int)r.Read(2);
        int offsetSize = sizes >> 12, lengthSize = (sizes >> 8) & 0xF, baseSize = (sizes >> 4) & 0xF;
        var indexSize = version is 1 or 2 ? sizes & 0xF : 0;
        var count = r.Read(version < 2 ? 2 : 4);
        for (var i = 0L; i < count; i++)
        {
            var id = r.Read(version < 2 ? 2 : 4);
            var method = version is 1 or 2 ? r.Read(2) & 0xF : 0;
            r.Skip(2); // data_reference_index
            var baseOffset = r.Read(baseSize);
            var extents = r.Read(2);
            using var bytes = new MemoryStream();
            for (var k = 0L; k < extents; k++)
            {
                r.Skip(indexSize);
                var offset = baseOffset + r.Read(offsetSize);
                var length = r.Read(lengthSize);
                if (id == wanted && !CopyExtent(file, method, idat, offset, length, bytes))
                {
                    return null;
                }
            }

            if (id == wanted)
            {
                return bytes.ToArray();
            }
        }

        return null;
    }

    private static bool CopyExtent(byte[] file, long method, HeifBox? idat, long offset, long length, MemoryStream into)
    {
        long start, limit;
        if (method == 0)
        {
            (start, limit) = (offset, file.Length);
        }
        else if (method == 1 && idat is { } d)
        {
            (start, limit) = (d.Payload + offset, d.End);
        }
        else
        {
            return false;
        }

        if (length is <= 0 || start < 0 || start + length > limit || into.Length + length > MaxExifBytes)
        {
            return false;
        }

        into.Write(file, (int)start, (int)length);
        return true;
    }
}
