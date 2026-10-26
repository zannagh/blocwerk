// <copyright file="PanelMoveScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>What every carried pair of one staged panel shares when its move is planned.</summary>
/// <param name="Size">The staged photo's size, or null.</param>
/// <param name="MmPerPx">The panel's scale, or null.</param>
/// <param name="Pairs">The panel's carried pairs, old centre → new centre, in normalized units.</param>
/// <param name="NoiseMm">The panel's typical photo shift (<see cref="HoldMoves.HoldMoveTolerance.PanelNoiseMm"/>), or null.</param>
internal sealed record PanelMoveScope((int Width, int Height)? Size, double? MmPerPx, IReadOnlyList<PointPair> Pairs, double? NoiseMm);
