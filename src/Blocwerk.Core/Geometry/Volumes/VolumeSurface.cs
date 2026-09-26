// <copyright file="VolumeSurface.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// A volume's shape as a height field over its parent facet: height above the facet plane (mm, ≥ 0) at the
/// centre of each grid cell, 0 outside the volume. Together with the facet plane under it this is a closed
/// solid (top surface, side walls down to the wall where the height drops to 0, and the base on the wall).
/// Heights are whole millimetres, stored as little-endian int16 in base64 (a 0.2 m² volume at 20 mm cells is
/// about 1 KB). A volume with "flat sides" also carries its <see cref="Polyhedron"/> (format version 2): heights and
/// normals then come from its planar faces exactly, and the grid holds the same shape rasterised.
/// </summary>
public sealed class VolumeSurface
{
    /// <summary>Largest grid accepted from storage.</summary>
    public const int MaxCells = 250_000;

    /// <summary>How far above a flat face a hold's photo ray may pass and still put the hold on it, mm (a hold's body).</summary>
    public const double FlatGraceMm = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly short[] heights;

    /// <summary>Initializes a new instance of the <see cref="VolumeSurface"/> class.</summary>
    /// <param name="grid">The grid (cell centres carry the heights).</param>
    /// <param name="heights">Height per cell, mm; row-major along a.</param>
    /// <param name="polyhedron">The flat-sided shape, when the volume has flat sides.</param>
    public VolumeSurface(CellGrid grid, short[] heights, VolumePolyhedron? polyhedron = null)
    {
        if (heights.Length != grid.Cols * grid.Rows)
        {
            throw new ArgumentException("one height per cell", nameof(heights));
        }

        Grid = grid;
        this.heights = heights;
        Polyhedron = polyhedron;
    }

    /// <summary>The grid.</summary>
    public CellGrid Grid { get; }

    /// <summary>The flat-sided shape, or null for a plain height field.</summary>
    public VolumePolyhedron? Polyhedron { get; }

    /// <summary>The heights, mm, row-major along a.</summary>
    public IReadOnlyList<short> Heights => heights;

    /// <summary>The tallest cell, mm.</summary>
    public double MaxHeightMm => Math.Max(heights.Length == 0 ? 0 : heights.Max(), Polyhedron?.TopHeightMm ?? 0);

    /// <summary>The height at plane point (a, b): bilinear between cell centres, 0 outside the grid.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>Height above the facet, mm.</returns>
    public double HeightAt(double a, double b)
    {
        if (Polyhedron is not null)
        {
            return Polyhedron.HeightAt(a, b);
        }

        var x = ((a - Grid.ALo) / Grid.CellMm) - 0.5;
        var y = ((b - Grid.BLo) / Grid.CellMm) - 0.5;
        if (x < 0 || y < 0 || x > Grid.Cols - 1 || y > Grid.Rows - 1 || Grid.Cols < 2 || Grid.Rows < 2)
        {
            return 0;
        }

        int i = Math.Min((int)x, Grid.Cols - 2), j = Math.Min((int)y, Grid.Rows - 2);
        double fx = x - i, fy = y - j;
        return (At(i, j) * (1 - fx) * (1 - fy)) + (At(i + 1, j) * fx * (1 - fy))
            + (At(i, j + 1) * (1 - fx) * fy) + (At(i + 1, j + 1) * fx * fy);
    }

    /// <summary>The outward unit normal of the surface at (a, b) in facet coordinates (a, b, height).</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>The normal; (0, 0, 1) on flat ground.</returns>
    public double[] NormalAt(double a, double b)
    {
        if (Polyhedron is not null)
        {
            return Polyhedron.NormalAt(a, b);
        }

        var d = Grid.CellMm;
        var ga = (HeightAt(a + d, b) - HeightAt(a - d, b)) / (2 * d);
        var gb = (HeightAt(a, b + d) - HeightAt(a, b - d)) / (2 * d);
        var len = Math.Sqrt((ga * ga) + (gb * gb) + 1);
        return [-ga / len, -gb / len, 1 / len];
    }

    /// <summary>
    /// Where the straight line from <paramref name="from"/> (facet coordinates, in front of the wall) to the
    /// plane point (a, b, 0) first meets the surface; null when it never does above <paramref name="minHeightMm"/>.
    /// On flat faces a ray that passes less than <see cref="FlatGraceMm"/> above a face without meeting it lands where
    /// it came closest: the hold's own body stands on the sheet, and the scanned height field included it.
    /// </summary>
    /// <param name="from">The ray's origin (a camera), (a, b, height).</param>
    /// <param name="a">Target along u.</param>
    /// <param name="b">Target along v.</param>
    /// <param name="minHeightMm">Hits lower than this are the wall around the volume, not the volume.</param>
    /// <returns>The hit (a, b, height).</returns>
    public (double A, double B, double H)? RayHit((double A, double B, double H) from, double a, double b, double minHeightMm)
    {
        double da = a - from.A, db = b - from.B, dh = -from.H;
        var length = Math.Sqrt((da * da) + (db * db) + (dh * dh));
        if (from.H <= 0 || length <= 0)
        {
            return null;
        }

        var clearance = Polyhedron is null ? -1 : FlatGraceMm;
        var top = MaxHeightMm + 1 + Math.Max(0, clearance);
        var t = Math.Max(0, (from.H - top) / from.H);
        var step = 1.0 / length;
        (double A, double B, double H)? closest = null;
        for (; t <= 1; t += step)
        {
            double pa = from.A + (t * da), pb = from.B + (t * db), ph = from.H + (t * dh);
            var h = HeightAt(pa, pb);
            if (ph <= h)
            {
                return ph >= minHeightMm ? (pa, pb, ph) : closest;
            }

            if (h >= minHeightMm && ph - h <= clearance)
            {
                (clearance, closest) = (ph - h, (pa, pb, h));
            }
        }

        return closest;
    }

    /// <summary>The storage form.</summary>
    /// <returns>JSON.</returns>
    public string ToJson()
    {
        var bytes = new byte[heights.Length * 2];
        for (var k = 0; k < heights.Length; k++)
        {
            bytes[2 * k] = (byte)(heights[k] & 0xff);
            bytes[(2 * k) + 1] = (byte)((heights[k] >> 8) & 0xff);
        }

        var faces = Polyhedron?.ToArrays();
        return JsonSerializer.Serialize(
            new VolumeSurfaceDocument(faces is null ? 1 : 2, Grid.ALo, Grid.BLo, Grid.CellMm, Grid.Cols, Grid.Rows, Convert.ToBase64String(bytes), faces, Polyhedron?.StoredShape),
            Json);
    }

    /// <summary>Parses the storage form; null when malformed.</summary>
    /// <param name="json">JSON.</param>
    /// <returns>The surface or null.</returns>
    public static VolumeSurface? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var d = JsonSerializer.Deserialize<VolumeSurfaceDocument>(json, Json);
            if (d is null || d.Version is not (1 or 2) || d.Cols <= 0 || d.Rows <= 0 || (long)d.Cols * d.Rows > MaxCells || !(d.CellMm > 0))
            {
                return null;
            }

            var bytes = Convert.FromBase64String(d.Heights);
            if (bytes.Length != d.Cols * d.Rows * 2)
            {
                return null;
            }

            var h = new short[d.Cols * d.Rows];
            for (var k = 0; k < h.Length; k++)
            {
                h[k] = (short)(bytes[2 * k] | (bytes[(2 * k) + 1] << 8));
            }

            var polyhedron = d.Version == 2 ? VolumePolyhedron.FromArrays(d.Faces, d.Shape) : null;
            return d.Version == 2 && polyhedron is null ? null : new VolumeSurface(new CellGrid(d.ALo, d.BLo, d.CellMm, d.Cols, d.Rows), h, polyhedron);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A flat-sided volume's surface: the grid covers its base (one cell of margin), heights rasterised from the faces.</summary>
    /// <param name="polyhedron">The shape.</param>
    /// <param name="cellMm">Grid cell side, mm.</param>
    /// <returns>The surface.</returns>
    public static VolumeSurface FlatSided(VolumePolyhedron polyhedron, double cellMm)
    {
        var ring = polyhedron.Base;
        var grid = CellGrid.Covering(
            ring.Min(p => p.A) - cellMm, ring.Max(p => p.A) + cellMm, ring.Min(p => p.B) - cellMm, ring.Max(p => p.B) + cellMm, cellMm);
        var h = new short[grid.Cols * grid.Rows];
        for (var k = 0; k < h.Length; k++)
        {
            var (a, b) = grid.Centre(k);
            h[k] = (short)Math.Clamp(Math.Round(polyhedron.HeightAt(a, b)), 0, short.MaxValue);
        }

        return new VolumeSurface(grid, h, polyhedron);
    }

    private double At(int i, int j) => heights[(j * Grid.Cols) + i];
}
