using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>Hand-built image bytes for the parser hardening tests: PNG chunks with real CRCs, trailers, a progressive JPEG.</summary>
internal static class HostileImages
{
    /// <summary>A 24×16 progressive JPEG (cjpeg -progressive -restart 1): 10 scans, DRI and RSTn markers in the data.</summary>
    public static readonly byte[] ProgressiveJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYn"
        + "KSopGR8tMC0oMCUoKSj/2wBDAQcHBwoIChMKChMoGhYaKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgoKCgo"
        + "KCgoKCgoKCj/wgARCAAQABgDASIAAhEBAxEB/8QAFgABAQEAAAAAAAAAAAAAAAAABQAG/8QAFgEBAQEAAAAAAAAAAAAAAAAABAEG"
        + "/90ABAAC/9oADAMBAAIQAxAAAAHEqPqUgFspOk//xAAYEAADAQEAAAAAAAAAAAAAAAAAAwQCEf/dAAQAA//aAAgBAQABBQJc4ucX"
        + "Of/QXOLnMT8P/8QAGREAAgMBAAAAAAAAAAAAAAAAAAECAwUE/90ABAAC/9oACAEDAQE/AeTSKdBuJ//EABoRAAICAwAAAAAAAAAA"
        + "AAAAAAEDAAIEIYH/2gAIAQIBAT8BUyHLUvV7Adn/xAAUEAEAAAAAAAAAAAAAAAAAAAAQ/90ABAAD/9oACAEBAAY/An//0H//xAAY"
        + "EAADAQEAAAAAAAAAAAAAAAAAAWERIf/aAAgBAQABPyGJEif/0IkRSa1w/90ABAAC/9oADAMBAAIAAwAAABAf/wD/xAAYEQACAwAA"
        + "AAAAAAAAAAAAAAAAAREhMf/aAAgBAwEBPxDKxIhn/8QAGBEBAQADAAAAAAAAAAAAAAAAAQARUbH/2gAIAQIBAT8QjGrHYOt//8QA"
        + "GBAAAwEBAAAAAAAAAAAAAAAAADHBARH/3QAEAAP/2gAIAQEAAT8QXIuRcn//0FyLk4HZh7w//9k=");

    /// <summary>What a Motion Photo appends after the JPEG: an MP4 with a location atom.</summary>
    public static readonly byte[] MotionPhotoTrailer =
    [
        .. new byte[] { 0, 0, 0, 0x18 }, .. "ftypmp42"u8.ToArray(), .. new byte[12],
        .. "©xyz+48.1372+011.5756/"u8.ToArray(), .. new byte[64],
    ];

    /// <summary>An APP2 Multi-Picture-Format segment (offsets of the appended images).</summary>
    public static byte[] MpfSegment() => Segment(0xE2, [.. "MPF\0"u8.ToArray(), .. "MM\0*"u8.ToArray(), .. new byte[24]]);

    public static byte[] Segment(byte marker, byte[] payload)
    {
        var s = new byte[4 + payload.Length];
        s[0] = 0xFF;
        s[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(s.AsSpan(2), (ushort)(payload.Length + 2));
        payload.CopyTo(s, 4);
        return s;
    }

    /// <summary>A PNG whose IHDR claims <paramref name="width"/> × <paramref name="height"/> but carries almost no data.</summary>
    public static byte[] PngClaiming(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        return [.. PngSignature, .. Chunk("IHDR", ihdr), .. Chunk("IDAT", [0x78, 0x9C, 0x03, 0, 0, 0, 0, 1]), .. Chunk("IEND", [])];
    }

    /// <summary>A PNG with an arbitrary raw chunk length field (no CRC check reaches it).</summary>
    public static byte[] PngWithRawChunk(uint length, string type, int payloadBytes)
    {
        var raw = new byte[8 + payloadBytes + 4];
        BinaryPrimitives.WriteUInt32BigEndian(raw, length);
        Encoding.ASCII.GetBytes(type).CopyTo(raw, 4);
        return [.. PngClaiming(32, 32)[..33], .. raw, .. Chunk("IEND", [])];
    }

    public static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    /// <summary>The decoded pixels, for "the stripped file shows exactly the same image".</summary>
    public static byte[] Pixels(byte[] encoded)
    {
        using var bitmap = SKBitmap.Decode(encoded) ?? throw new InvalidOperationException("not decodable");
        return bitmap.Bytes;
    }

    /// <summary>Runs <paramref name="action"/> on the thread pool and fails the test if it has not returned within 5 s.</summary>
    public static async Task<T> Bounded<T>(Func<T> action) =>
        await Task.Run(action).WaitAsync(TimeSpan.FromSeconds(5));

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static uint Crc32(ReadOnlySpan<byte> bytes)
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
