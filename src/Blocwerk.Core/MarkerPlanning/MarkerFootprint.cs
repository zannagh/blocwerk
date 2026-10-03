// <copyright file="MarkerFootprint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>Wall area a plan's markers occupy, and what one-size-fits-all printing would take instead.</summary>
/// <param name="MarkerCount">Markers counted.</param>
/// <param name="AreaCm2">Wall covered by the cut-outs (black square plus white border), cm².</param>
/// <param name="UniformSizeMm">The largest size in the plan — what every marker would be printed at in a one-size plan.</param>
/// <param name="UniformAreaCm2">Wall covered if every marker were <see cref="UniformSizeMm"/>, cm².</param>
public sealed record MarkerFootprint(int MarkerCount, double AreaCm2, double UniformSizeMm, double UniformAreaCm2)
{
    /// <summary>Share of wall saved compared with the one-size plan (0..1).</summary>
    public double Saving => UniformAreaCm2 > 0 ? 1 - (AreaCm2 / UniformAreaCm2) : 0;

    /// <summary>
    /// Side of one marker's cut-out: the black square plus the white border the PDF prints (the mounting
    /// holes' border when they are on, else a one-module quiet zone of side/6).
    /// </summary>
    public static double CutOutSideMm(double sizeMm, PrintOptions? print) =>
        sizeMm + (2 * (print is { HolesEnabled: true, MountingHoles: { } holes }
            ? MountingHoleLayout.BorderMm(holes)
            : sizeMm / ArucoDict4X4.ModulesPerSide));

    /// <summary>The footprint of <paramref name="plan"/>'s markers.</summary>
    public static MarkerFootprint Of(MarkerPlan plan)
    {
        var sizes = plan.Markers.Select(m => m.SizeMm).Where(s => double.IsFinite(s) && s > 0).ToList();
        if (sizes.Count == 0)
        {
            return new MarkerFootprint(0, 0, 0, 0);
        }

        var area = sizes.Sum(s => Square(CutOutSideMm(s, plan.Print))) / 100;
        var uniform = sizes.Max();
        return new MarkerFootprint(sizes.Count, area, uniform, sizes.Count * Square(CutOutSideMm(uniform, plan.Print)) / 100);
    }

    private static double Square(double v) => v * v;
}
