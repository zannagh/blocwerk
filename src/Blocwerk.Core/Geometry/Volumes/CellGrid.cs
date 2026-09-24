// <copyright file="CellGrid.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>A regular grid on a facet plane: cell (i, j) covers a ∈ [ALo + i·cell, …), b ∈ [BLo + j·cell, …).</summary>
/// <param name="ALo">Lower a edge, mm.</param>
/// <param name="BLo">Lower b edge, mm.</param>
/// <param name="CellMm">Cell side, mm.</param>
/// <param name="Cols">Cells along a.</param>
/// <param name="Rows">Cells along b.</param>
public sealed record CellGrid(double ALo, double BLo, double CellMm, int Cols, int Rows)
{
    private static readonly (int Di, int Dj)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>A grid covering [aMin, aMax] × [bMin, bMax].</summary>
    /// <param name="aMin">Lower a.</param>
    /// <param name="aMax">Upper a.</param>
    /// <param name="bMin">Lower b.</param>
    /// <param name="bMax">Upper b.</param>
    /// <param name="cellMm">Cell side.</param>
    /// <returns>The grid.</returns>
    public static CellGrid Covering(double aMin, double aMax, double bMin, double bMax, double cellMm) =>
        new(aMin, bMin, cellMm, (int)Math.Floor((aMax - aMin) / cellMm) + 1, (int)Math.Floor((bMax - bMin) / cellMm) + 1);

    /// <summary>The flat index of the cell holding (a, b), or −1 outside.</summary>
    /// <param name="a">Along u.</param>
    /// <param name="b">Along v.</param>
    /// <returns>The index.</returns>
    public int IndexOf(double a, double b)
    {
        var i = (int)Math.Floor((a - ALo) / CellMm);
        var j = (int)Math.Floor((b - BLo) / CellMm);
        return i < 0 || j < 0 || i >= Cols || j >= Rows ? -1 : (j * Cols) + i;
    }

    /// <summary>The centre of a cell.</summary>
    /// <param name="index">Flat index.</param>
    /// <returns>(a, b).</returns>
    public (double A, double B) Centre(int index) =>
        (ALo + (((index % Cols) + 0.5) * CellMm), BLo + (((index / Cols) + 0.5) * CellMm));

    /// <summary>Per cell, the <paramref name="q"/> quantile of the values falling in it; NaN with fewer than <paramref name="minPoints"/>.</summary>
    /// <param name="a">Along u.</param>
    /// <param name="b">Along v.</param>
    /// <param name="values">One value per point.</param>
    /// <param name="q">Quantile in [0, 1].</param>
    /// <param name="minPoints">Fewest points per cell.</param>
    /// <returns>The grid of quantiles.</returns>
    public double[] Quantile(IReadOnlyList<double> a, IReadOnlyList<double> b, IReadOnlyList<double> values, double q, int minPoints)
    {
        var buckets = new Dictionary<int, List<double>>();
        for (var k = 0; k < values.Count; k++)
        {
            var idx = IndexOf(a[k], b[k]);
            if (idx < 0)
            {
                continue;
            }

            if (!buckets.TryGetValue(idx, out var list))
            {
                list = [];
                buckets[idx] = list;
            }

            list.Add(values[k]);
        }

        var result = new double[Cols * Rows];
        Array.Fill(result, double.NaN);
        foreach (var (idx, list) in buckets)
        {
            if (list.Count >= minPoints)
            {
                list.Sort();
                result[idx] = list[(int)(q * (list.Count - 1))];
            }
        }

        return result;
    }

    /// <summary>Morphological closing (3×3) then opening (2×2) of a mask.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The cleaned mask.</returns>
    public bool[] CloseOpen(bool[] mask)
    {
        var closed = Morph(Morph(mask, -1, 1, true), -1, 1, false);
        return Morph(Morph(closed, 0, 1, false), -1, 0, true);
    }

    /// <summary>Dilation by the square offsets [lo, hi]² (cells outside count as empty).</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="lo">Lowest offset.</param>
    /// <param name="hi">Highest offset.</param>
    /// <returns>The dilated mask.</returns>
    public bool[] Dilate(bool[] mask, int lo, int hi) => Morph(mask, lo, hi, true);

    /// <summary>4-connected components of a mask: label per cell (0 = none) and the count.</summary>
    /// <param name="mask">The mask.</param>
    /// <returns>The labels and how many.</returns>
    public (int[] Labels, int Count) Label(bool[] mask)
    {
        var labels = new int[mask.Length];
        var count = 0;
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] != 0)
            {
                continue;
            }

            labels[start] = ++count;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                int i = c % Cols, j = c / Cols;
                foreach (var (di, dj) in Neighbours)
                {
                    int ni = i + di, nj = j + dj;
                    var n = (nj * Cols) + ni;
                    if (ni >= 0 && nj >= 0 && ni < Cols && nj < Rows && mask[n] && labels[n] == 0)
                    {
                        labels[n] = count;
                        stack.Push(n);
                    }
                }
            }
        }

        return (labels, count);
    }

    private bool[] Morph(bool[] mask, int lo, int hi, bool dilate)
    {
        var result = new bool[mask.Length];
        for (var j = 0; j < Rows; j++)
        {
            for (var i = 0; i < Cols; i++)
            {
                result[(j * Cols) + i] = Window(mask, i, j, lo, hi, dilate);
            }
        }

        return result;
    }

    private bool Window(bool[] mask, int i, int j, int lo, int hi, bool dilate)
    {
        for (var dj = lo; dj <= hi; dj++)
        {
            for (var di = lo; di <= hi; di++)
            {
                int ni = i + di, nj = j + dj;
                var v = ni >= 0 && nj >= 0 && ni < Cols && nj < Rows && mask[(nj * Cols) + ni];
                if (dilate && v)
                {
                    return true;
                }

                if (!dilate && !v)
                {
                    return false;
                }
            }
        }

        return !dilate;
    }
}
