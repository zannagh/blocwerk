// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Blocwerk.Core.Geometry.Sparse;

/// <summary>
/// Reads the splat worker's <c>sparse.zip</c> (COLMAP's binary <c>images.bin</c> + <c>points3D.bin</c> and
/// <c>stems.json</c>, see <c>docker/splat-worker/splatworker/sparse_export.py</c>) into a <see cref="SparseCloud"/>. The 2D
/// observations are skipped, never kept. Throws <see cref="InvalidDataException"/> on anything malformed.
/// </summary>
public static class ColmapSparseReader
{
    /// <summary>Points seen by fewer images are not kept (COLMAP keeps two-view points, the noisiest).</summary>
    public const int MinTrack = 2;

    private const long MaxEntryBytes = 1L << 30;

    /// <summary>Reads a sparse.zip.</summary>
    /// <param name="zip">The zip bytes.</param>
    /// <returns>The cloud.</returns>
    public static SparseCloud Read(byte[] zip)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var roles = Roles(Entry(archive, "stems.json"));
            var centres = PhotoCentres(Entry(archive, "images.bin"), roles);
            var (xyz, error, track) = Points(Entry(archive, "points3D.bin"));
            return new SparseCloud(centres, xyz, error, track);
        }
        catch (Exception ex) when (ex is EndOfStreamException or JsonException or IOException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("sparse.zip is not a readable COLMAP model: " + ex.Message, ex);
        }
    }

    private static byte[] Entry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"sparse.zip has no {name}");
        if (entry.Length > MaxEntryBytes)
        {
            throw new InvalidDataException($"{name} in sparse.zip is too large");
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>COLMAP image name → (stem, role).</summary>
    private static Dictionary<string, (string Stem, string Role)> Roles(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!doc.RootElement.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var image in images.EnumerateObject())
        {
            var stem = image.Value.TryGetProperty("stem", out var s) ? s.GetString() : null;
            var role = image.Value.TryGetProperty("role", out var r) ? r.GetString() : null;
            if (!string.IsNullOrEmpty(stem))
            {
                result[image.Name] = (stem, role ?? "photo");
            }
        }

        return result;
    }

    /// <summary>The camera centre (−Rᵀt) of every image whose role is "photo", by stem.</summary>
    private static Dictionary<string, double[]> PhotoCentres(byte[] bin, Dictionary<string, (string Stem, string Role)> roles)
    {
        using var reader = new BinaryReader(new MemoryStream(bin));
        var count = reader.ReadUInt64();
        var result = new Dictionary<string, double[]>(StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            reader.ReadInt32();
            double qw = reader.ReadDouble(), qx = reader.ReadDouble(), qy = reader.ReadDouble(), qz = reader.ReadDouble();
            double[] t = [reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()];
            reader.ReadInt32();
            var name = ReadName(reader);
            var observations = reader.ReadUInt64();
            reader.BaseStream.Seek(checked((long)observations * 24), SeekOrigin.Current);
            if (reader.BaseStream.Position > reader.BaseStream.Length)
            {
                throw new EndOfStreamException("images.bin is truncated");
            }

            var (stem, role) = roles.TryGetValue(name, out var known) ? known : (Path.GetFileNameWithoutExtension(name), "photo");
            if (role == "photo")
            {
                result[stem] = Centre(qw, qx, qy, qz, t);
            }
        }

        return result;
    }

    private static string ReadName(BinaryReader reader)
    {
        var bytes = new List<byte>();
        for (var b = reader.ReadByte(); b != 0; b = reader.ReadByte())
        {
            bytes.Add(b);
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }

    /// <summary>−Rᵀt for the world→camera rotation of the unit quaternion (w, x, y, z).</summary>
    private static double[] Centre(double w, double x, double y, double z, double[] t)
    {
        var n = Math.Sqrt((w * w) + (x * x) + (y * y) + (z * z));
        (w, x, y, z) = (w / n, x / n, y / n, z / n);
        double[] r =
        [
            1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) - (w * z)), 2 * ((x * z) + (w * y)),
            2 * ((x * y) + (w * z)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) - (w * x)),
            2 * ((x * z) - (w * y)), 2 * ((y * z) + (w * x)), 1 - (2 * ((x * x) + (y * y))),
        ];
        return
        [
            -((r[0] * t[0]) + (r[3] * t[1]) + (r[6] * t[2])),
            -((r[1] * t[0]) + (r[4] * t[1]) + (r[7] * t[2])),
            -((r[2] * t[0]) + (r[5] * t[1]) + (r[8] * t[2])),
        ];
    }

    private static (float[] Xyz, float[] Error, ushort[] Track) Points(byte[] bin)
    {
        using var reader = new BinaryReader(new MemoryStream(bin));
        var count = reader.ReadUInt64();
        var xyz = new List<float>();
        var error = new List<float>();
        var track = new List<ushort>();
        for (ulong i = 0; i < count; i++)
        {
            reader.ReadUInt64();
            double x = reader.ReadDouble(), y = reader.ReadDouble(), z = reader.ReadDouble();
            reader.ReadBytes(3);
            var err = reader.ReadDouble();
            var length = reader.ReadUInt64();
            reader.BaseStream.Seek(checked((long)length * 8), SeekOrigin.Current);
            if (reader.BaseStream.Position > reader.BaseStream.Length)
            {
                throw new EndOfStreamException("points3D.bin is truncated");
            }

            if (length < MinTrack || !double.IsFinite(x + y + z + err))
            {
                continue;
            }

            xyz.Add((float)x);
            xyz.Add((float)y);
            xyz.Add((float)z);
            error.Add((float)err);
            track.Add((ushort)Math.Min(length, ushort.MaxValue));
        }

        return ([.. xyz], [.. error], [.. track]);
    }
}
