// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Recognition.Worker.Jobs;

/// <summary>A <see cref="HoldSeed"/> on the wire, with the id its outline is echoed under.</summary>
public sealed record OutlineRequestSeed(int Id, double X, double Y, double Radius, double? BoxWidth = null, double? BoxHeight = null)
{
    public HoldSeed ToSeed() => new(X, Y, Radius, BoxWidth, BoxHeight);
}
