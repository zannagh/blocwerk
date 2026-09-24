// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.IO.Compression;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Builds the mobile level of detail of a Gaussian-splat scene: the <c>.spz</c> (Niantic SPZ v2/v3, a
/// gzip stream of a 16-byte header and attribute-major arrays) pruned to its <c>target</c> most visible
/// splats. Visibility is opacity × the area of the splat's two largest axes, so the faint and tiny
/// splats go first; the survivors keep their original order and every attribute byte-for-byte.
/// </summary>
/// <remarks>
/// A phone's Safari tab has a fraction of a desktop's GPU budget and a hard frame-time watchdog: a
/// half-million-splat capture (walk-along video included) is sorted and blended every frame and can
/// lose the WebGL context there, which leaves the canvas black. 180k keeps the wall sharp while
/// staying near the size of the earlier photo-only captures (~115k).
/// </remarks>
public static class SpzDecimator
{
    /// <summary>Splat count of the mobile level of detail.</summary>
    public const int MobileTarget = 180_000;

    /// <summary>Scenes up to this many splats are light enough as they are and get no mobile copy.</summary>
    public const int MobileThreshold = 220_000;

    private const uint Magic = 0x5053474e;   // "NGSP"
    private const int HeaderBytes = 16;

    /// <summary>Splat count of an <c>.spz</c>; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static int CountOf(byte[] spz) => ReadHeader(Decompress(spz)).Count;

    /// <summary>
    /// The scene pruned to <paramref name="target"/> splats, gzip-compressed like the input, or null
    /// when it has no more than <paramref name="threshold"/> splats (nothing worth pruning).
    /// </summary>
    public static byte[]? Decimate(byte[] spz, int target = MobileTarget, int threshold = MobileThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(target, 1);
        var raw = Decompress(spz);
        var header = ReadHeader(raw);
        if (header.Count <= Math.Max(target, threshold))
        {
            return null;
        }

        return Encode(raw, header, Keep(Ranked(raw, header), target));
    }

    /// <summary>
    /// A level-of-detail ladder: the scene pruned to each of <paramref name="targets"/> splats, ranked
    /// once. Only levels of at most <paramref name="maxFraction"/> of the full count are built (a level
    /// barely smaller than the full scene saves nothing); ascending by splat count.
    /// </summary>
    public static IReadOnlyList<(int Splats, byte[] Spz)> Ladder(byte[] spz, IEnumerable<int> targets, double maxFraction)
    {
        var raw = Decompress(spz);
        var header = ReadHeader(raw);
        var wanted = targets.Where(t => t >= 1 && t <= header.Count * maxFraction).Distinct().Order().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var ranked = Ranked(raw, header);
        return wanted.Select(t => (t, Encode(raw, header, Keep(ranked, t)))).ToList();
    }

    /// <summary>
    /// The decompressed scene with its splat count and the byte offsets of the position, alpha and
    /// scale arrays (for readers of the raw attributes, e.g. <see cref="SpzPoints"/>).
    /// </summary>
    internal static (byte[] Raw, int Count, int PositionOffset, int AlphaOffset, int ScaleOffset) Open(byte[] spz)
    {
        var raw = Decompress(spz);
        var header = ReadHeader(raw);
        return (raw, header.Count, HeaderBytes, header.AlphaOffset, header.ScaleOffset);
    }

    private static byte[] Encode(byte[] raw, SpzHeader header, int[] keep)
    {
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var head = raw.AsSpan(0, HeaderBytes).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(8, 4), (uint)keep.Length);
            gzip.Write(head);
            var offset = HeaderBytes;
            foreach (var stride in header.Strides)
            {
                WriteSubset(gzip, raw.AsSpan(offset, stride * header.Count), stride, keep);
                offset += stride * header.Count;
            }
        }

        return output.ToArray();
    }

    /// <summary>Indices (ascending) of the <paramref name="target"/> last (most visible) of <paramref name="ranked"/>.</summary>
    private static int[] Keep(int[] ranked, int target)
    {
        var keep = ranked.AsSpan(ranked.Length - target).ToArray();
        Array.Sort(keep);
        return keep;
    }

    /// <summary>Every splat index, least visible first.</summary>
    private static int[] Ranked(byte[] raw, SpzHeader header)
    {
        var n = header.Count;
        var alphas = raw.AsSpan(header.AlphaOffset, n);
        var scales = raw.AsSpan(header.ScaleOffset, n * 3);
        var score = new float[n];
        var order = new int[n];
        for (var i = 0; i < n; i++)
        {
            // Scales are log-encoded (s / 16 − 10); alpha is the post-sigmoid opacity × 255.
            int a = scales[3 * i], b = scales[(3 * i) + 1], c = scales[(3 * i) + 2];
            var twoLargest = a + b + c - Math.Min(a, Math.Min(b, c));
            score[i] = MathF.Log((alphas[i] + 0.5f) / 255.5f) + (twoLargest / 16f);
            order[i] = i;
        }

        Array.Sort(score, order);
        return order;
    }

    private static void WriteSubset(Stream to, ReadOnlySpan<byte> column, int stride, int[] keep)
    {
        var buffer = new byte[keep.Length * stride];
        for (var k = 0; k < keep.Length; k++)
        {
            column.Slice(keep[k] * stride, stride).CopyTo(buffer.AsSpan(k * stride, stride));
        }

        to.Write(buffer);
    }

    private static byte[] Decompress(byte[] spz)
    {
        if (spz.Length < 2 || spz[0] != 0x1f || spz[1] != 0x8b)
        {
            throw new InvalidDataException("not a gzip-compressed .spz file.");
        }

        using var input = new GZipStream(new MemoryStream(spz), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        input.CopyTo(raw);
        return raw.ToArray();
    }

    private static SpzHeader ReadHeader(byte[] raw)
    {
        if (raw.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(raw) != Magic)
        {
            throw new InvalidDataException("not an .spz file (bad magic).");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8));
        var shDegree = raw[12];
        if (version is < 2 or > 3 || shDegree > 3 || count > int.MaxValue / 64)
        {
            throw new InvalidDataException($"unsupported .spz (version {version}, SH degree {shDegree}).");
        }

        var shCoefficients = shDegree switch { 0 => 0, 1 => 3, 2 => 8, _ => 15 };
        int[] strides = [9, 1, 3, 3, version == 2 ? 3 : 4, shCoefficients * 3];
        var header = new SpzHeader((int)count, strides);
        if (raw.Length < HeaderBytes + (strides.Sum() * header.Count))
        {
            throw new InvalidDataException("truncated .spz file.");
        }

        return header;
    }

    /// <summary>Splat count and per-splat byte strides: positions, alphas, colours, scales, rotations, SH.</summary>
    private sealed record SpzHeader(int Count, int[] Strides)
    {
        public int AlphaOffset => HeaderBytes + (Strides[0] * Count);

        public int ScaleOffset => AlphaOffset + ((Strides[1] + Strides[2]) * Count);
    }
}
