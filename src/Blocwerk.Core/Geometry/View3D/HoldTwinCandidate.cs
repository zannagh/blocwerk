// <copyright file="HoldTwinCandidate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>A placed hold row that may be one panel's copy of a hold another panel also shows.</summary>
/// <param name="Hold">The stored hold row.</param>
/// <param name="Placed">Its placed 3D hold (position, size, shape, role in the highlighted boulder).</param>
/// <param name="Tilt">How obliquely its panel photo saw its facet (<see cref="PhotoViewTilt"/>); null when unknown.</param>
public sealed record HoldTwinCandidate(Hold Hold, Wall3DHold Placed, double? Tilt);

/// <summary>The physical holds found among the placed rows.</summary>
/// <param name="Groups">One group per physical hold; the first member is the representative that is drawn.</param>
/// <param name="ExplicitMerges">Rows folded into another through a stored <see cref="HoldLink"/>.</param>
/// <param name="GeometricMerges">Rows folded into another by the geometric fallback.</param>
/// <param name="RejectedLinks">Stored links ignored because their two holds sit implausibly far apart.</param>
public sealed record HoldTwinGroups(
    IReadOnlyList<IReadOnlyList<HoldTwinCandidate>> Groups,
    int ExplicitMerges,
    int GeometricMerges,
    int RejectedLinks);
