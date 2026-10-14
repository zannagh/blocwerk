// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// One box of a hold detection result, in the photo's pixels, exactly as the app's tiled detector merges them (integer edges,
/// exclusive right and bottom): what a runner uploads per photo (<see cref="RunnerHoldsResult"/>).
/// </summary>
/// <param name="Left">Left edge.</param>
/// <param name="Top">Top edge.</param>
/// <param name="Right">Right edge.</param>
/// <param name="Bottom">Bottom edge.</param>
/// <param name="Confidence">The model's confidence.</param>
/// <param name="Label">The class: <c>hold</c> or <c>volume</c>.</param>
public readonly record struct RunnerHoldBox(int Left, int Top, int Right, int Bottom, double Confidence, string Label);
