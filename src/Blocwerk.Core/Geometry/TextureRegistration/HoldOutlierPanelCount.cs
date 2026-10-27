// <copyright file="HoldOutlierPanelCount.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>One panel photo of an outlier check.</summary>
/// <param name="PanelId">The panel.</param>
/// <param name="ConsistencyMm">How well its holds' placements agree (<see cref="PhotoConsistency"/>); photos are checked best first.</param>
/// <param name="Checked">Placed holds looked at.</param>
/// <param name="FromTwins">Holds to re-place onto their linked copy on a better photo.</param>
/// <param name="FromNeighbours">Holds to re-place from their neighbours on the same photo.</param>
/// <param name="Undecided">Holds left alone because their neighbours were too few, split between facets or mapped off the model.</param>
public sealed record HoldOutlierPanelCount(Guid PanelId, double ConsistencyMm, int Checked, int FromTwins, int FromNeighbours, int Undecided);
