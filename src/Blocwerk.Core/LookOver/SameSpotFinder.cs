// <copyright file="SameSpotFinder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// Which new hold sits on the spot of an old hold that was kept in place. Looser on purpose than the duplicate-holds
/// review (<see cref="Detection.Outlines.HoldDuplicateFinder"/> wants a similar colour and outlines that mostly
/// overlap): after a re-set the hold on the spot is another one, so only the position is compared.
/// </summary>
internal static class SameSpotFinder
{
    /// <summary>
    /// The nearest of <paramref name="fresh"/> whose centre lies within the larger of the two radii of
    /// <paramref name="kept"/>'s centre, or null.
    /// </summary>
    /// <param name="kept">The old hold's copy on the new photo.</param>
    /// <param name="fresh">The new holds of the same photo.</param>
    /// <param name="size">The photo's pixel size (positions are normalised per axis, radii to the long side); null treats it as square.</param>
    /// <returns>The hold on the spot.</returns>
    public static Hold? Nearest(Hold kept, IEnumerable<Hold> fresh, (int Width, int Height)? size)
    {
        var (width, height) = size is { Width: > 0, Height: > 0 } s ? ((double)s.Width, (double)s.Height) : (1d, 1d);
        var longSide = Math.Max(width, height);
        Hold? best = null;
        var bestDistance = double.MaxValue;
        foreach (var other in fresh)
        {
            var dx = (kept.X - other.X) * width;
            var dy = (kept.Y - other.Y) * height;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance <= Math.Max(kept.Radius, other.Radius) * longSide && distance < bestDistance)
            {
                (best, bestDistance) = (other, distance);
            }
        }

        return best;
    }
}
