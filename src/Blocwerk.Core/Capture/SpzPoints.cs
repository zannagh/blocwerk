// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>
/// The splat centres of an <c>.spz</c> scene that look like surface (opaque enough, small enough),
/// moved into the wall world by the frame's splat → world matrix. Input for measurements on the
/// captured surface, e.g. <see cref="Geometry.View3D.HoldProtrusionEstimator"/>.
/// </summary>
public static class SpzPoints
{
    /// <summary>Faintest splat kept (post-sigmoid opacity; most splats of a trained scene are faint).</summary>
    public const double MinAlpha = 0.1;

    /// <summary>Widest splat kept, mm: anything wider is fog, not surface.</summary>
    public const double MaxSplatMm = 40;

    /// <summary>With <c>includeFlat</c>: a flat splat (thinnest axis at most this, mm) is surface even when wide.</summary>
    public const double FlatThinMm = 4;

    /// <summary>With <c>includeFlat</c>: the widest flat splat kept, mm (smooth volume faces carry wide flat splats).</summary>
    public const double FlatMaxMm = 150;

    /// <summary>With <c>includeFlat</c>: the faintest flat splat kept.</summary>
    public const double FlatMinAlpha = 0.3;

    /// <summary>
    /// Surface-like centres in world mm. <paramref name="worldMatrix"/> is the column-major 4×4 of
    /// <see cref="CaptureSplatDocuments.WorldMatrix"/> (a similarity: its scale turns splat sizes into mm).
    /// </summary>
    /// <param name="spz">The gzip-compressed scene.</param>
    /// <param name="worldMatrix">Column-major splat → world (mm).</param>
    /// <param name="includeFlat">Also keep wide but thin, opaque splats (volume detection: smooth faces are drawn with them).</param>
    /// <returns>The kept centres.</returns>
    public static List<(float X, float Y, float Z)> Read(byte[] spz, IReadOnlyList<double> worldMatrix, bool includeFlat = false)
    {
        var (raw, n, positionOffset, alphaOffset, scaleOffset) = SpzDecimator.Open(spz);
        var scale = 1.0 / (1 << raw[13]);
        var m = worldMatrix;
        var mmPerUnit = Math.Cbrt(Math.Abs(Det3(m)));
        var alphas = raw.AsSpan(alphaOffset, n);
        var scales = raw.AsSpan(scaleOffset, n * 3);
        var positions = raw.AsSpan(positionOffset, n * 9);
        var minAlpha = (byte)Math.Ceiling(MinAlpha * 255);
        var maxLog = (int)Math.Floor((Math.Log(MaxSplatMm / mmPerUnit) + 10) * 16);
        var flatThin = (int)Math.Floor((Math.Log(FlatThinMm / mmPerUnit) + 10) * 16);
        var flatMax = includeFlat ? (int)Math.Floor((Math.Log(FlatMaxMm / mmPerUnit) + 10) * 16) : -1;
        var flatAlpha = (byte)Math.Ceiling(FlatMinAlpha * 255);
        var result = new List<(float, float, float)>();
        for (var i = 0; i < n; i++)
        {
            var widest = Math.Max(scales[3 * i], Math.Max(scales[(3 * i) + 1], scales[(3 * i) + 2]));
            var small = alphas[i] >= minAlpha && widest <= maxLog;
            var flat = alphas[i] >= flatAlpha && widest <= flatMax
                && Math.Min(scales[3 * i], Math.Min(scales[(3 * i) + 1], scales[(3 * i) + 2])) <= flatThin;
            if (!small && !flat)
            {
                continue;
            }

            var x = Fixed24(positions, 9 * i) * scale;
            var y = Fixed24(positions, (9 * i) + 3) * scale;
            var z = Fixed24(positions, (9 * i) + 6) * scale;
            result.Add((
                (float)((m[0] * x) + (m[4] * y) + (m[8] * z) + m[12]),
                (float)((m[1] * x) + (m[5] * y) + (m[9] * z) + m[13]),
                (float)((m[2] * x) + (m[6] * y) + (m[10] * z) + m[14])));
        }

        return result;
    }

    private static int Fixed24(ReadOnlySpan<byte> bytes, int at)
    {
        var v = bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16);
        return (v & 0x800000) != 0 ? v - (1 << 24) : v;
    }

    private static double Det3(IReadOnlyList<double> m) =>
        (m[0] * ((m[5] * m[10]) - (m[9] * m[6])))
        - (m[4] * ((m[1] * m[10]) - (m[9] * m[2])))
        + (m[8] * ((m[1] * m[6]) - (m[5] * m[2])));
}
