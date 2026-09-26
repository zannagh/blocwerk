// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Synthetic EXIF with an Apple maker note (never a real photo): a TIFF block whose EXIF IFD holds a MakerNote
/// ("Apple iOS\0", version 1, "MM", an IFD at 14 with RunTimeVersion-like 0x0001 and AccelerationVector 0x0008 as three
/// SRATIONALs, offsets relative to the maker note), wrapped into a JPEG APP1 or a minimal HEIF (ftyp, meta with iinf/iloc,
/// the Exif item in mdat or idat).
/// </summary>
internal static class AppleMakerNoteExif
{
    private const int Denominator = 100_000_000;

    /// <summary>The value the reader returns for <paramref name="v"/> (the rational it is written as).</summary>
    public static double Written(double v) => (int)Math.Round(v * Denominator) / (double)Denominator;

    /// <summary>An Apple maker note with the vector (or, with <paramref name="vector"/> null, without tag 0x0008).</summary>
    public static byte[] MakerNote((double X, double Y, double Z)? vector, string header = "Apple iOS\0")
    {
        var note = new List<byte>();
        note.AddRange(Encoding.ASCII.GetBytes(header));
        note.AddRange(U16(1, false));
        note.AddRange("MM"u8.ToArray());
        var entries = vector is null ? 1 : 2;
        var values = 14 + 2 + (entries * 12) + 4;
        note.AddRange(U16((ushort)entries, false));
        note.AddRange(Entry(0x0001, 9, 1, 14, false));
        if (vector is not null)
        {
            note.AddRange(Entry(0x0008, 10, 3, (uint)values, false));
        }

        note.AddRange(U32(0, false));
        if (vector is { } a)
        {
            foreach (var c in new[] { a.X, a.Y, a.Z })
            {
                note.AddRange(U32((uint)(int)Math.Round(c * Denominator), false));
                note.AddRange(U32(Denominator, false));
            }
        }

        return [.. note];
    }

    /// <summary>A TIFF block: IFD0 (Make, the EXIF IFD pointer) → EXIF IFD (FocalLength-free, just the MakerNote, if any).</summary>
    public static byte[] Tiff(byte[]? makerNote, bool little = false)
    {
        var make = Encoding.ASCII.GetBytes("Apple\0");
        const int ifd0 = 8, ifd0Size = 2 + (2 * 12) + 4, exifIfd = ifd0 + ifd0Size;
        var exifEntries = makerNote is null ? 0 : 1;
        var values = exifIfd + 2 + (exifEntries * 12) + 4;
        var data = new List<byte>(little ? "II"u8.ToArray() : "MM"u8.ToArray());
        data.AddRange(U16(42, little));
        data.AddRange(U32(ifd0, little));
        data.AddRange(U16(2, little));
        data.AddRange(Entry(0x010F, 2, (uint)make.Length, (uint)values, little));
        data.AddRange(Entry(0x8769, 4, 1, exifIfd, little));
        data.AddRange(U32(0, little));
        data.AddRange(U16((ushort)exifEntries, little));
        if (makerNote is not null)
        {
            data.AddRange(Entry(0x927C, 7, (uint)makerNote.Length, (uint)(values + make.Length), little));
        }

        data.AddRange(U32(0, little));
        data.AddRange(make);
        data.AddRange(makerNote ?? Array.Empty<byte>());
        return [.. data];
    }

    /// <summary>The TIFF block as an EXIF APP1 right after the JPEG's SOI.</summary>
    public static byte[] Jpeg(byte[] jpeg, byte[] tiff)
    {
        byte[] payload = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        var segment = new byte[4 + payload.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(segment, 4);
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    /// <summary>A minimal HEIF whose Exif item (id 2) holds the TIFF block: in mdat (iloc v0) or in idat (iloc v1, method 1).</summary>
    public static byte[] Heic(byte[] tiff, bool inIdat = false)
    {
        byte[] item = [.. U32(6, false), .. "Exif\0\0"u8.ToArray(), .. tiff];
        var ftyp = Box("ftyp", [.. "heic"u8.ToArray(), 0, 0, 0, 0, .. "mif1heic"u8.ToArray()]);
        var hvc = Box("infe", [2, 0, 0, 0, .. U16(1, false), 0, 0, .. "hvc1"u8.ToArray(), 0]);
        var exif = Box("infe", [2, 0, 0, 0, .. U16(2, false), 0, 0, .. "Exif"u8.ToArray(), 0]);
        var iinf = Box("iinf", [0, 0, 0, 0, .. U16(2, false), .. hvc, .. exif]);
        byte[] Meta(uint offset)
        {
            byte[] location = inIdat
                ? [1, 0, 0, 0, 0x44, 0x00, .. U16(1, false), .. U16(2, false), .. U16(1, false), .. U16(0, false), .. U16(1, false)]
                : [0, 0, 0, 0, 0x44, 0x00, .. U16(1, false), .. U16(2, false), .. U16(0, false), .. U16(1, false)];
            var iloc = Box("iloc", [.. location, .. U32(offset, false), .. U32((uint)item.Length, false)]);
            byte[] data = inIdat ? Box("idat", item) : [];
            return Box("meta", [0, 0, 0, 0, .. iinf, .. iloc, .. data]);
        }

        if (inIdat)
        {
            return [.. ftyp, .. Meta(0), .. Box("mdat", new byte[16])];
        }

        var at = (uint)(ftyp.Length + Meta(0).Length + 8);
        return [.. ftyp, .. Meta(at), .. Box("mdat", item)];
    }

    private static byte[] Box(string type, byte[] payload) =>
        [.. U32((uint)(payload.Length + 8), false), .. Encoding.ASCII.GetBytes(type), .. payload];

    private static byte[] Entry(ushort tag, ushort type, uint count, uint value, bool little) =>
        [.. U16(tag, little), .. U16(type, little), .. U32(count, little), .. U32(value, little)];

    private static byte[] U16(ushort v, bool little)
    {
        var b = new byte[2];
        if (little)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(b, v);
        }

        return b;
    }

    private static byte[] U32(uint v, bool little)
    {
        var b = new byte[4];
        if (little)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
        }

        return b;
    }
}
