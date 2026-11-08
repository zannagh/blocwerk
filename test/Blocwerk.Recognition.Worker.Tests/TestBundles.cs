// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>Input envelopes and images for the tests.</summary>
internal static class TestBundles
{
    public static byte[] Png(int width = 4, int height = 4)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Gray);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A PNG whose header claims <paramref name="width"/> x <paramref name="height"/>; no decodable pixels.</summary>
    public static byte[] PngHeader(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A recognition envelope (protocol §2.1): the files in order, then the request part.</summary>
    public static byte[] Envelope(string kind, object request, params (string Name, string File, byte[] Bytes)[] files)
    {
        var parts = files.Select(f => (object)new { name = f.Name, file = f.File }).Append(new { name = "request", json = request });
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "parts.json", JsonSerializer.SerializeToUtf8Bytes(new { version = 1, service = "recognition", kind, parts }));
            foreach (var (_, file, bytes) in files)
            {
                Add(zip, file, bytes);
            }
        }

        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] bytes)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
        entry.Write(bytes);
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
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
