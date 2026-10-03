// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;

namespace Blocwerk.Core.Geometry.Sparse;

/// <summary>
/// The stored form of a <see cref="SparseCloud"/> (<c>.spts</c> in the capture store): gzip of a small binary layout
/// (magic, version, the photo centres by stem, then the points as float x/y/z, float error, ushort track).
/// </summary>
public static class SparseCloudFile
{
    /// <summary>The file extension in the capture store.</summary>
    public const string Extension = ".spts";

    private const string Magic = "BWSPARSE";
    private const int Version = 1;

    /// <summary>Serialises a cloud.</summary>
    /// <param name="cloud">The cloud.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(SparseCloud cloud)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(gzip))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(cloud.PhotoCentres.Count);
            foreach (var (stem, c) in cloud.PhotoCentres.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                writer.Write(stem);
                writer.Write(c[0]);
                writer.Write(c[1]);
                writer.Write(c[2]);
            }

            writer.Write(cloud.Count);
            for (var i = 0; i < cloud.Count; i++)
            {
                writer.Write(cloud.Xyz[3 * i]);
                writer.Write(cloud.Xyz[(3 * i) + 1]);
                writer.Write(cloud.Xyz[(3 * i) + 2]);
                writer.Write(cloud.Error[i]);
                writer.Write(cloud.Track[i]);
            }
        }

        return output.ToArray();
    }

    /// <summary>Reads a stored cloud; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    /// <param name="bytes">The stored bytes.</param>
    /// <returns>The cloud.</returns>
    public static SparseCloud Read(byte[] bytes)
    {
        try
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new BinaryReader(gzip);
            if (reader.ReadString() != Magic || reader.ReadInt32() != Version)
            {
                throw new InvalidDataException("not a sparse point file");
            }

            var cameras = reader.ReadInt32();
            var centres = new Dictionary<string, double[]>(StringComparer.Ordinal);
            for (var i = 0; i < cameras; i++)
            {
                var stem = reader.ReadString();
                centres[stem] = [reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble()];
            }

            var count = reader.ReadInt32();
            var xyz = new float[3 * count];
            var error = new float[count];
            var track = new ushort[count];
            for (var i = 0; i < count; i++)
            {
                xyz[3 * i] = reader.ReadSingle();
                xyz[(3 * i) + 1] = reader.ReadSingle();
                xyz[(3 * i) + 2] = reader.ReadSingle();
                error[i] = reader.ReadSingle();
                track[i] = reader.ReadUInt16();
            }

            return new SparseCloud(centres, xyz, error, track);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or OverflowException or OutOfMemoryException)
        {
            throw new InvalidDataException("the sparse point file is damaged: " + ex.Message, ex);
        }
    }
}
