// <copyright file="HoldSeeds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Abstractions;

/// <summary>Builds the outliner's seed for a hold.</summary>
public static class HoldSeeds
{
    /// <summary>The seed of a hold: its detector box when it has one and boxes are used, else its radius circle.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="useBox">False to always seed with the radius circle.</param>
    /// <returns>The seed.</returns>
    public static HoldSeed Of(Hold hold, bool useBox = true)
    {
        ArgumentNullException.ThrowIfNull(hold);
        return useBox && hold.DetectedBox is { Width: > 0, Height: > 0 } box
            ? new HoldSeed(hold.X, hold.Y, hold.Radius, box.Width, box.Height)
            : new HoldSeed(hold.X, hold.Y, hold.Radius);
    }
}
