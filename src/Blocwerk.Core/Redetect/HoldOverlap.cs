// <copyright file="HoldOverlap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Redetect;

/// <summary>How two hold outlines on one photo lie to each other.</summary>
/// <param name="Iou">Shared area over combined area.</param>
/// <param name="Containment">Shared area over the smaller outline's area.</param>
/// <param name="SizeRatio">The larger over the smaller, measured across (square root of the area ratio); at least 1.</param>
/// <param name="CentreOffset">The distance between the two centres as a fraction of the smaller outline's radius.</param>
public readonly record struct HoldOverlap(double Iou, double Containment, double SizeRatio, double CentreOffset)
{
    /// <summary>From this share of the smaller outline inside the larger one, the smaller counts as lying inside it.</summary>
    public const double InsideFrom = 0.9;

    /// <summary>
    /// Gets one number for "same place and same size": the IoU, or, when the smaller outline lies inside the larger one
    /// (a traced outline inside the circle that stood for the hold before), its share inside divided by the size ratio.
    /// </summary>
    public double Score => Math.Max(Iou, Containment >= InsideFrom ? Containment / Math.Max(1, SizeRatio) : 0);
}
