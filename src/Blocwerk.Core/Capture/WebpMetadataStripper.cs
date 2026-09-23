// <copyright file="WebpMetadataStripper.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Buffers.Binary;
using System.Text;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Removes metadata from a WebP without re-encoding: the RIFF chunks are walked with bounded,
/// length-checked steps and only the image chunks (VP8X, VP8, VP8L, ALPH, ANIM, ANMF — plus ICCP with
/// keepColour) are copied byte for byte. EXIF (GPS, device), XMP, unknown chunks and anything past the
/// RIFF payload are dropped; the VP8X flags are cleared to match what is left and the RIFF size is
/// rewritten.
/// </summary>
public static class WebpMetadataStripper
{
    private const byte IccFlag = 0x20;
    private const byte ExifFlag = 0x08;
    private const byte XmpFlag = 0x04;
    private const int HeaderLength = 12;
    private const int ChunkHeaderLength = 8;
    private const int Vp8xPayloadLength = 10;

    /// <summary>A walk never visits more chunks than this, whatever the declared sizes say.</summary>
    private const int MaxChunks = 4096;

    /// <summary>"RIFF" .... "WEBP".</summary>
    public static bool IsWebp(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= HeaderLength && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>The stripped file; throws <see cref="InvalidDataException"/> when it is not a well-formed WebP.</summary>
    public static byte[] Strip(byte[] src, bool keepColour)
    {
        var chunks = Parse(src);
        using var output = new MemoryStream(src.Length);
        output.Write(src, 0, HeaderLength);
        var flagsAt = -1;
        var keptIcc = false;
        foreach (var chunk in chunks.Where(c => Keep(c.FourCc, keepColour)))
        {
            keptIcc |= chunk.FourCc == "ICCP";
            if (chunk.FourCc == "VP8X")
            {
                flagsAt = (int)output.Position + ChunkHeaderLength;
            }

            WriteChunk(output, src.AsSpan(chunk.Start, ChunkHeaderLength + chunk.Size));
        }

        var bytes = output.ToArray();
        if (flagsAt >= 0)
        {
            bytes[flagsAt] &= unchecked((byte)~(ExifFlag | XmpFlag | (keptIcc ? 0 : IccFlag)));
        }

        return WithRiffSize(bytes);
    }

    /// <summary>The EXIF chunk's TIFF data (an "Exif\0\0" prefix skipped), or empty. Never throws.</summary>
    public static ReadOnlySpan<byte> FindExif(byte[] src)
    {
        try
        {
            foreach (var chunk in Parse(src))
            {
                if (chunk.FourCc == "EXIF")
                {
                    var payload = src.AsSpan(chunk.Start + ChunkHeaderLength, chunk.Size);
                    return payload.StartsWith("Exif\0\0"u8) ? payload[6..] : payload;
                }
            }
        }
        catch (InvalidDataException)
        {
            return [];
        }

        return [];
    }

    /// <summary>
    /// Appends an EXIF chunk holding <paramref name="tiff"/> and sets the VP8X EXIF flag. A simple-format
    /// WebP (no VP8X, so no place to declare EXIF) is returned unchanged.
    /// </summary>
    public static byte[] AppendExif(byte[] webp, ReadOnlySpan<byte> tiff)
    {
        var chunks = Parse(webp);
        if (chunks[0].FourCc != "VP8X")
        {
            return webp;
        }

        using var output = new MemoryStream(webp.Length + tiff.Length + ChunkHeaderLength + 1);
        output.Write(webp, 0, 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(4)));
        var header = new byte[ChunkHeaderLength];
        "EXIF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)tiff.Length);
        output.Write(header);
        WriteChunkBody(output, tiff);
        var bytes = output.ToArray();
        bytes[chunks[0].Start + ChunkHeaderLength] |= ExifFlag;
        return WithRiffSize(bytes);
    }

    /// <summary>
    /// The top-level chunks. Every size is unsigned and checked against the RIFF payload before it is
    /// used, each step moves forward by at least a header, and the walk stops after <see cref="MaxChunks"/>.
    /// </summary>
    internal static List<WebpChunk> Parse(byte[] src)
    {
        if (!IsWebp(src))
        {
            throw new InvalidDataException("Not a WebP file.");
        }

        var riff = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(4));
        if (riff < 4 || riff > (uint)(src.Length - 8))
        {
            throw new InvalidDataException("The WebP is truncated (RIFF size out of range).");
        }

        var end = 8 + (int)riff;
        var pos = HeaderLength;
        var chunks = new List<WebpChunk>();
        while (pos < end)
        {
            if (chunks.Count >= MaxChunks || end - pos < ChunkHeaderLength)
            {
                throw new InvalidDataException("The WebP is corrupt (chunk header out of range).");
            }

            var size = BinaryPrimitives.ReadUInt32LittleEndian(src.AsSpan(pos + 4));
            if (size > (uint)(end - pos - ChunkHeaderLength))
            {
                throw new InvalidDataException("The WebP is corrupt (chunk length out of range).");
            }

            chunks.Add(new WebpChunk(Encoding.ASCII.GetString(src, pos, 4), pos, (int)size));

            // A missing pad byte after the LAST chunk is tolerated (some writers omit it).
            pos = (int)Math.Min(end, pos + ChunkHeaderLength + (long)size + (size & 1));
        }

        Validate(chunks);
        return chunks;
    }

    private static void Validate(List<WebpChunk> chunks)
    {
        if (!chunks.Any(c => c.FourCc is "VP8 " or "VP8L" or "ANMF"))
        {
            throw new InvalidDataException("The WebP is truncated (no image data).");
        }

        var vp8x = chunks.FindIndex(c => c.FourCc == "VP8X");
        if (vp8x > 0 || (vp8x == 0 && chunks[0].Size < Vp8xPayloadLength))
        {
            throw new InvalidDataException("The WebP is corrupt (misplaced or short VP8X chunk).");
        }
    }

    private static bool Keep(string fourCc, bool keepColour) =>
        fourCc is "VP8X" or "VP8 " or "VP8L" or "ALPH" or "ANIM" or "ANMF"
        || (keepColour && fourCc == "ICCP");

    /// <summary>Header + payload, then a zero pad byte when the payload is odd.</summary>
    private static void WriteChunk(MemoryStream output, ReadOnlySpan<byte> headerAndPayload)
    {
        output.Write(headerAndPayload[..ChunkHeaderLength]);
        WriteChunkBody(output, headerAndPayload[ChunkHeaderLength..]);
    }

    private static void WriteChunkBody(MemoryStream output, ReadOnlySpan<byte> payload)
    {
        output.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static byte[] WithRiffSize(byte[] bytes)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }
}
