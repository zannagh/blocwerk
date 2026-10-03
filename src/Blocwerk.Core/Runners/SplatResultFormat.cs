// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Content check of a trained splat a runner uploads, before anything else touches it: a binary little-endian PLY
/// with exactly one <c>element vertex</c> of float 3DGS columns (known names only), whose size matches its header to the
/// byte and whose sampled values are finite; or an <c>.spz</c> (gzip, <c>NGSP</c> magic) that is decompressed here,
/// streamed and capped, and must be exactly as long as its header says (so no nested bomb reaches the splat worker).
/// </summary>
public static class SplatResultFormat
{
    public const string Ply = "ply";
    public const string Spz = "spz";

    /// <summary>The default cap on splats (PLY vertices / SPZ points).</summary>
    public const long DefaultMaxSplats = 12_000_000;

    /// <summary>At most this many vertices are read back for the NaN/Inf check, spread evenly over the file.</summary>
    public const int FiniteSampleVertices = 65536;

    private const int MaxHeaderBytes = 16 * 1024;
    private const string EndHeader = "end_header\n";
    private static readonly string[] RequiredPlyColumns = ["x", "y", "z", "opacity", "scale_0", "rot_0", "f_dc_0"];
    private static readonly HashSet<string> AllowedPlyColumns = BuildAllowedColumns();

    /// <summary>The format (<see cref="Ply"/> or <see cref="Spz"/>), or throws <see cref="InvalidDataException"/>.</summary>
    public static string Validate(string path, long maxSplats = DefaultMaxSplats)
    {
        using var file = File.OpenRead(path);
        var head = new byte[Math.Min(MaxHeaderBytes, file.Length)];
        file.ReadExactly(head);
        if (head.Length >= 2 && head[0] == 0x1f && head[1] == 0x8b)
        {
            file.Position = 0;
            SpzFormat.Validate(file, maxSplats);
            return Spz;
        }

        var (headerBytes, vertices, columns) = ParsePlyHeader(head, maxSplats);
        var expected = checked(headerBytes + checked(vertices * columns * 4));
        if (file.Length != expected)
        {
            throw new InvalidDataException(file.Length < expected ? "The .ply is truncated." : "The .ply has data past its vertices.");
        }

        CheckFinite(file, headerBytes, vertices, columns);
        return Ply;
    }

    private static (long HeaderBytes, long Vertices, int Columns) ParsePlyHeader(byte[] head, long maxSplats)
    {
        var text = Encoding.ASCII.GetString(head);
        var end = text.IndexOf(EndHeader, StringComparison.Ordinal);
        if (!text.StartsWith("ply\n", StringComparison.Ordinal) || end < 0)
        {
            throw new InvalidDataException("The upload is neither a .ply nor an .spz splat.");
        }

        var lines = text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count < 2 || lines[1] != "format binary_little_endian 1.0")
        {
            throw new InvalidDataException("The .ply must be binary little-endian.");
        }

        long vertices = -1;
        var columns = new List<string>();
        foreach (var line in lines.Skip(2).Where(l => !l.StartsWith("comment ", StringComparison.Ordinal) && !l.StartsWith("obj_info ", StringComparison.Ordinal)))
        {
            var parts = line.Split(' ');
            if (parts is ["element", "vertex", var n] && vertices < 0 && columns.Count == 0 && long.TryParse(n, out var count))
            {
                vertices = count;
            }
            else if (parts is ["property", "float", var name] && vertices >= 0 && AllowedPlyColumns.Contains(name) && !columns.Contains(name))
            {
                columns.Add(name);
            }
            else
            {
                throw new InvalidDataException($"The .ply header has an unexpected line: {Clip(line)}");
            }
        }

        if (vertices <= 0 || vertices > maxSplats || RequiredPlyColumns.Any(c => !columns.Contains(c)))
        {
            throw new InvalidDataException(
                $"The .ply is not a 3D Gaussian splat of 1 to {maxSplats} splats (float columns x, y, z, opacity, scale, rot, f_dc).");
        }

        return (end + EndHeader.Length, vertices, columns.Count);
    }

    /// <summary>Reads up to <see cref="FiniteSampleVertices"/> evenly spread vertices; any NaN or infinity refuses the file.</summary>
    private static void CheckFinite(FileStream file, long headerBytes, long vertices, int columns)
    {
        var record = new byte[columns * 4];
        var stride = Math.Max(1, vertices / FiniteSampleVertices);
        for (long v = 0; v < vertices; v += stride)
        {
            file.Position = headerBytes + (v * record.Length);
            file.ReadExactly(record);
            for (var c = 0; c < columns; c++)
            {
                if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(record.AsSpan(c * 4, 4))))
                {
                    throw new InvalidDataException($"The .ply has a NaN or infinite value (vertex {v}).");
                }
            }
        }
    }

    private static HashSet<string> BuildAllowedColumns()
    {
        var names = new HashSet<string>(StringComparer.Ordinal) { "x", "y", "z", "nx", "ny", "nz", "opacity" };
        names.UnionWith(Enumerable.Range(0, 3).Select(i => $"f_dc_{i}"));
        names.UnionWith(Enumerable.Range(0, 45).Select(i => $"f_rest_{i}"));
        names.UnionWith(Enumerable.Range(0, 3).Select(i => $"scale_{i}"));
        names.UnionWith(Enumerable.Range(0, 4).Select(i => $"rot_{i}"));
        return names;
    }

    private static string Clip(string line) => line.Length <= 60 ? line : line[..60];
}
