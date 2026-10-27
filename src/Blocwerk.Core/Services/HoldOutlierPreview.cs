// <copyright file="HoldOutlierPreview.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>What "re-place outlier holds in 3D" would do, without writing anything.</summary>
/// <param name="Checked">Placed holds looked at.</param>
/// <param name="FromTwins">Holds it would move onto their linked copy on a more consistent photo.</param>
/// <param name="FromNeighbours">Holds it would move to where their neighbours on the same photo put them.</param>
/// <param name="Undecided">Holds it cannot judge (too few or split neighbours, or an estimate off the model).</param>
/// <param name="Panels">Per panel photo, in the order they are checked (most consistent first).</param>
public sealed record HoldOutlierPreview(int Checked, int FromTwins, int FromNeighbours, int Undecided, IReadOnlyList<HoldOutlierPanelCount> Panels)
{
    /// <summary>Gets the holds it would re-place.</summary>
    public int ToReplace => FromTwins + FromNeighbours;

    /// <summary>The preview of a plan.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>Its counts.</returns>
    public static HoldOutlierPreview Of(HoldOutlierPlan plan) => new(
        plan.Panels.Sum(p => p.Checked),
        plan.Panels.Sum(p => p.FromTwins),
        plan.Panels.Sum(p => p.FromNeighbours),
        plan.Panels.Sum(p => p.Undecided),
        plan.Panels);
}
