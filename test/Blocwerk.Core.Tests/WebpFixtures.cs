// <copyright file="WebpFixtures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Buffers.Binary;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>Hand-assembled extended WebPs around a real Skia-encoded image chunk.</summary>
internal static class WebpFixtures
{
    public const byte IccFlag = 0x20;
    public const byte ExifFlag = 0x08;
    public const byte XmpFlag = 0x04;

    /// <summary>An odd-length (so padded) fake ICC profile.</summary>
    public static readonly byte[] Icc = [.. "fake-icc-profile"u8, 1, 2, 3];

    /// <summary>A Skia-encoded simple WebP (just a VP8 chunk, no metadata).</summary>
    public static byte[] Simple(int width = 24, int height = 16) => TestImages.Noise(width, height, SKEncodedImageFormat.Webp);

    /// <summary>
    /// VP8X (ICC|EXIF|XMP), ICCP, the image chunk, EXIF (GPS + Orientation 6 + an iPhone make),
    /// XMP with a GPS field, an unknown chunk with a comment, then trailing junk past the RIFF payload.
    /// </summary>
    public static byte[] PhoneWebp(bool withIcc = true, byte[]? exif = null)
    {
        var simple = Simple();
        var image = simple.AsSpan(12).ToArray();
        byte flags = (byte)(ExifFlag | XmpFlag | (withIcc ? IccFlag : 0));
        List<byte> body = [.. Chunk("VP8X", Vp8x(flags, 24, 16))];
        if (withIcc)
        {
            body.AddRange(Chunk("ICCP", Icc));
        }

        body.AddRange(image);
        body.AddRange(Chunk("EXIF", exif ?? [.. "Exif\0\0"u8, .. PrivacyPhotos.ExifTiff()]));
        body.AddRange(Chunk("XMP ", "<x:xmpmeta><exif:GPSLatitude>48,8N</exif:GPSLatitude></x:xmpmeta>"u8.ToArray()));
        body.AddRange(Chunk("ZZZZ", "shot at home"u8.ToArray()));
        return [.. Riff([.. body]), .. "trailing junk"u8];
    }

    public static byte[] Riff(byte[] body)
    {
        var file = new byte[12 + body.Length];
        "RIFF"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), (uint)(body.Length + 4));
        "WEBP"u8.CopyTo(file.AsSpan(8));
        body.CopyTo(file, 12);
        return file;
    }

    public static byte[] Chunk(string fourCc, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length + (payload.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(fourCc).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    /// <summary>The top-level (FourCC, payload) list, walked the simple way (fixtures are well-formed).</summary>
    public static List<(string FourCc, byte[] Payload)> Chunks(byte[] webp)
    {
        var list = new List<(string, byte[])>();
        var end = 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4));
        for (var pos = 12; pos + 8 <= end;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(pos + 4));
            list.Add((System.Text.Encoding.ASCII.GetString(webp, pos, 4), webp.AsSpan(pos + 8, size).ToArray()));
            pos += 8 + size + (size & 1);
        }

        return list;
    }

    private static byte[] Vp8x(byte flags, int width, int height)
    {
        var payload = new byte[10];
        payload[0] = flags;
        WriteUInt24(payload.AsSpan(4), width - 1);
        WriteUInt24(payload.AsSpan(7), height - 1);
        return payload;
    }

    private static void WriteUInt24(Span<byte> at, int value)
    {
        at[0] = (byte)value;
        at[1] = (byte)(value >> 8);
        at[2] = (byte)(value >> 16);
    }
}
