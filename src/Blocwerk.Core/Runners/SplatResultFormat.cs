// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Content check of a trained splat a runner uploads, before anything else touches it: a binary
/// little-endian PLY of float vertex properties with the 3DGS columns (whose size matches its header),
/// or an <c>.spz</c> (gzip, <c>NGSP</c> magic).
/// </summary>
public static class SplatResultFormat
{
    public const string Ply = "ply";
    public const string Spz = "spz";

    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly string[] RequiredPlyColumns = ["x", "y", "z", "opacity", "scale_0", "rot_0", "f_dc_0"];

    /// <summary>The format (<see cref="Ply"/> or <see cref="Spz"/>), or throws <see cref="InvalidDataException"/>.</summary>
    public static string Validate(string path)
    {
        using var file = File.OpenRead(path);
        var head = new byte[Math.Min(MaxHeaderBytes, file.Length)];
        file.ReadExactly(head);
        if (head.Length >= 2 && head[0] == 0x1f && head[1] == 0x8b)
        {
            file.Position = 0;
            ValidateSpz(file);
            return Spz;
        }

        ValidatePly(head, file.Length);
        return Ply;
    }

    private static void ValidateSpz(Stream file)
    {
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var header = new byte[16];
        gzip.ReadExactly(header);
        var magic = BitConverter.ToUInt32(header, 0);
        var version = BitConverter.ToUInt32(header, 4);
        var count = BitConverter.ToUInt32(header, 8);
        if (magic != 0x5053474E || version is < 2 or > 3 || count == 0)
        {
            throw new InvalidDataException("The upload is not a valid .spz splat.");
        }
    }

    private static void ValidatePly(byte[] head, long length)
    {
        var text = Encoding.ASCII.GetString(head);
        var end = text.IndexOf("end_header\n", StringComparison.Ordinal);
        if (!text.StartsWith("ply\n", StringComparison.Ordinal) || end < 0)
        {
            throw new InvalidDataException("The upload is neither a .ply nor an .spz splat.");
        }

        var lines = text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!lines.Contains("format binary_little_endian 1.0"))
        {
            throw new InvalidDataException("The .ply must be binary little-endian.");
        }

        var vertices = lines.Where(l => l.StartsWith("element vertex ", StringComparison.Ordinal))
            .Select(l => long.TryParse(l["element vertex ".Length..], out var n) ? n : -1).FirstOrDefault(-1);
        var props = lines.Where(l => l.StartsWith("property ", StringComparison.Ordinal)).Select(l => l.Split(' ')).ToList();
        if (vertices <= 0 || props.Any(p => p.Length != 3 || p[1] != "float")
            || RequiredPlyColumns.Any(c => props.All(p => p[2] != c)))
        {
            throw new InvalidDataException("The .ply is not a 3D Gaussian splat (float vertex columns x, y, z, opacity, scale, rot, f_dc).");
        }

        var expected = end + "end_header\n".Length + (vertices * props.Count * 4);
        if (length < expected)
        {
            throw new InvalidDataException("The .ply is truncated.");
        }
    }
}
