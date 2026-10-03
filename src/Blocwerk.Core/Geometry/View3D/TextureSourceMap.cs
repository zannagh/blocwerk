// <copyright file="TextureSourceMap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Which capture photo painted each part of a facet texture, as the geometry worker returns it next to the
/// texture (<c>docker/wall-geometry/wallgeometry/sourcemap.py</c>): a coarse grid of cells over the facet
/// plane, each naming one camera of the geometry model the texture was made from. A protruding hold shows
/// in the texture as seen FROM that camera, so the 3D view uses it to draw the hold's outline where the
/// photo shows it (<see cref="HoldPhotoOutline"/>).
/// </summary>
public sealed class TextureSourceMap
{
    /// <summary>Largest map accepted (a 5 × 4 m facet at 16 mm cells is ~0.1 MB).</summary>
    public const long MaxBytes = 8L * 1024 * 1024;

    private readonly ushort[] cells;

    private TextureSourceMap(double cellMm, double aMin, double bMax, int cols, int rows, IReadOnlyList<string> cameras, ushort[] cells)
    {
        CellMm = cellMm;
        AMin = aMin;
        BMax = bMax;
        Cols = cols;
        Rows = rows;
        Cameras = cameras;
        this.cells = cells;
    }

    /// <summary>Cell side on the facet plane, mm.</summary>
    public double CellMm { get; }

    /// <summary>Left edge of column 0 along the facet's u axis, mm.</summary>
    public double AMin { get; }

    /// <summary>Top edge of row 0 along the facet's v axis, mm.</summary>
    public double BMax { get; }

    /// <summary>Columns (along u).</summary>
    public int Cols { get; }

    /// <summary>Rows (down v).</summary>
    public int Rows { get; }

    /// <summary>The cameras the cells refer to (image names of the geometry model's <c>cameras</c>).</summary>
    public IReadOnlyList<string> Cameras { get; }

    /// <summary>Parses a map document, or null when it is not a well-formed version-1 map.</summary>
    /// <param name="json">The UTF-8 JSON bytes.</param>
    /// <returns>The map or null.</returns>
    public static TextureSourceMap? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Int(root, "version") != 1)
            {
                return null;
            }

            var (cols, rows, bits) = (Int(root, "cols"), Int(root, "rows"), Int(root, "bits"));
            var (cellMm, aMin, bMax) = (Num(root, "cellMm"), Num(root, "aMin"), Num(root, "bMax"));
            if (cols is not > 0 || rows is not > 0 || bits is not (8 or 16) || cellMm is not > 0 || aMin is null || bMax is null
                || (long)cols * rows > 16_000_000 || !root.TryGetProperty("cameras", out var camsEl) || camsEl.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("cells", out var cellsEl) || cellsEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var cameras = camsEl.EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList();
            var raw = Convert.FromBase64String(cellsEl.GetString()!);
            var n = cols.Value * rows.Value;
            if (raw.Length != n * (bits.Value / 8))
            {
                return null;
            }

            var values = new ushort[n];
            for (var i = 0; i < n; i++)
            {
                values[i] = bits == 8 ? raw[i] : (ushort)(raw[2 * i] | (raw[(2 * i) + 1] << 8));
            }

            return values.Any(v => v > cameras.Count)
                ? null
                : new TextureSourceMap(cellMm.Value, aMin.Value, bMax.Value, cols.Value, rows.Value, cameras, values);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The camera that painted plane point (a, b), or null outside the map or where no photo was used.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>The camera's image name or null.</returns>
    public string? CameraAt(double a, double b)
    {
        var i = (int)Math.Floor((a - AMin) / CellMm);
        var j = (int)Math.Floor((BMax - b) / CellMm);
        if (i < 0 || j < 0 || i >= Cols || j >= Rows)
        {
            return null;
        }

        var v = cells[(j * Cols) + i];
        return v == 0 ? null : Cameras[v - 1];
    }

    /// <summary>The camera that painted most of <paramref name="points"/> (ties: the first seen), or null.</summary>
    /// <param name="points">Plane points, mm.</param>
    /// <returns>The camera's image name or null.</returns>
    public string? Dominant(IEnumerable<(double A, double B)> points)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? best = null;
        foreach (var (a, b) in points)
        {
            if (CameraAt(a, b) is not { } cam)
            {
                continue;
            }

            counts[cam] = counts.GetValueOrDefault(cam) + 1;
            if (best is null || counts[cam] > counts[best])
            {
                best = cam;
            }
        }

        return best;
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : null;
}
