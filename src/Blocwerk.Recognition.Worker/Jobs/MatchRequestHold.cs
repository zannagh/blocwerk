// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>A <see cref="MatcherHold"/> on the wire: the outline as <c>[x, y]</c> pairs.</summary>
public sealed record MatchRequestHold(int Id, double X, double Y, double? SizeNorm = null, MatcherColor? Color = null, double[][]? Shape = null)
{
    public MatcherHold ToHold() => new(Id, X, Y, SizeNorm, Color, Shape?.Select(p => (p[0], p[1])).ToList());
}
