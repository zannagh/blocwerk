// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;
using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>The mobile level of detail of a splat scene keeps the most visible splats, byte-for-byte.</summary>
public class SpzDecimatorTests
{
    [Fact]
    public void Decimate_KeepsTheMostVisibleSplats_InOrder_WithEveryAttributeIntact()
    {
        var spz = Spz(1000, shDegree: 1);

        var lod = SpzDecimator.Decimate(spz, target: 100, threshold: 200)!;

        var raw = Gunzip(lod);
        Assert.Equal(100, SpzDecimator.CountOf(lod));
        Assert.Equal(Gunzip(spz).AsSpan(0, 8).ToArray(), raw.AsSpan(0, 8).ToArray());   // magic + version kept
        var kept = Enumerable.Range(0, 100).Select(k => Id(raw, 100, k)).ToList();
        Assert.Equal(FileOrder(1000).Where(kept.Contains), kept);                          // original order
        var cutoff = Enumerable.Range(0, 1000).Select(Visibility).OrderDescending().ElementAt(99);
        Assert.All(kept, id => Assert.True(Visibility(id) >= cutoff - 1e-4));             // the top tenth
        for (var k = 0; k < 100; k++)
        {
            AssertSplat(raw, 100, k, kept[k], shCoefficients: 3);
        }
    }

    [Fact]
    public void Decimate_LeavesASmallScene_Alone()
    {
        Assert.Null(SpzDecimator.Decimate(Spz(300, shDegree: 0), target: 100, threshold: 300));
    }

    [Fact]
    public void Decimate_RejectsWhatIsNotAnSpz()
    {
        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionMode.Compress, leaveOpen: true))
        {
            z.Write(new byte[64]);
        }

        Assert.Throws<InvalidDataException>(() => SpzDecimator.Decimate(gz.ToArray()));
        Assert.Throws<InvalidDataException>(() => SpzDecimator.Decimate([1, 2, 3]));
    }

    /// <summary>
    /// A synthetic SPZ v2 scene: splat i carries its id in its position bytes, and its visibility
    /// (opacity × area of the two largest axes) grows with i, with the ids shuffled across the file.
    /// </summary>
    internal static byte[] Spz(int count, int shDegree)
    {
        var sh = shDegree switch { 0 => 0, 1 => 3, 2 => 8, _ => 15 } * 3;
        var ids = FileOrder(count);
        var raw = new byte[16 + (count * (9 + 1 + 3 + 3 + 3 + sh))];
        BinaryPrimitives.WriteUInt32LittleEndian(raw, 0x5053474e);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), (uint)count);
        raw[12] = (byte)shDegree;
        raw[13] = 12;
        var o = 16;
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(o + (9 * i)), ids[i]);
        }

        o += 9 * count;
        for (var i = 0; i < count; i++)
        {
            raw[o + i] = (byte)(55 + (ids[i] % 200));                          // alpha
        }

        o += count;
        for (var i = 0; i < count * 3; i++)
        {
            raw[o + i] = (byte)(ids[i / 3] % 251);                              // colour
        }

        o += 3 * count;
        for (var i = 0; i < count; i++)
        {
            var s = (byte)(20 + (ids[i] * 200 / count));                        // scales: grow with the id
            raw[o + (3 * i)] = s;
            raw[o + (3 * i) + 1] = s;
            raw[o + (3 * i) + 2] = 10;
        }

        o += 3 * count;
        for (var i = 0; i < count * (3 + sh); i++)
        {
            raw[o + i] = (byte)((ids[i / (3 + sh)] + i) % 256);                  // rotations, then SH
        }

        using var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionMode.Compress, leaveOpen: true))
        {
            z.Write(raw);
        }

        return gz.ToArray();
    }

    private static int[] FileOrder(int count) => Enumerable.Range(0, count).OrderBy(i => (i * 7919) % count).ToArray();

    private static double Visibility(int id) => Math.Log((55 + (id % 200) + 0.5) / 255.5) + (2 * (20 + (id * 200 / 1000)) / 16.0);

    private static int Id(byte[] raw, int count, int k) => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(16 + (9 * k)));

    private static void AssertSplat(byte[] raw, int count, int k, int id, int shCoefficients)
    {
        var o = 16 + (9 * count);
        Assert.Equal((byte)(55 + (id % 200)), raw[o + k]);
        o += count;
        Assert.Equal((byte)(id % 251), raw[o + (3 * k)]);
        o += 3 * count;
        Assert.Equal((byte)(20 + (id * 200 / 1000)), raw[o + (3 * k)]);
        o += 3 * count;
        Assert.Equal(count * (3 + (shCoefficients * 3)), raw.Length - o);
    }

    private static byte[] Gunzip(byte[] gz)
    {
        using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        return raw.ToArray();
    }
}
