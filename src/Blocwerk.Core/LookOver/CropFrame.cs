// <copyright file="CropFrame.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.LookOver;

/// <summary>The square of a photo one look-over picture shows.</summary>
/// <param name="CentreX">Its centre from the photo's left edge, px.</param>
/// <param name="CentreY">Its centre from the photo's top edge, px.</param>
/// <param name="HalfPx">Half its side, px.</param>
/// <param name="RingPx">The radius of the ring around the spot, px (drawn when the picture has no outlines).</param>
/// <param name="Width">The photo's width, px.</param>
/// <param name="Height">The photo's height, px.</param>
internal readonly record struct CropFrame(double CentreX, double CentreY, double HalfPx, double RingPx, int Width, int Height)
{
    /// <summary>
    /// A hold of the photo as it lies in this picture: centre, radius and outline in the picture's own 0..1 square,
    /// which is what the page's hold renderer takes.
    /// </summary>
    /// <param name="hold">The hold (on this photo).</param>
    /// <param name="kept">Draw it as kept, not confirmed.</param>
    /// <returns>The outline.</returns>
    public LookOverOutline Outline(Hold hold, bool kept)
    {
        var (side, width, height) = (2 * HalfPx, (double)Width, (double)Height);
        ShapePoint Scaled(ShapePoint p) => new() { Dx = p.Dx * width / side, Dy = p.Dy * height / side };
        return new LookOverOutline(
            ((hold.X * Width) - (CentreX - HalfPx)) / side,
            ((hold.Y * Height) - (CentreY - HalfPx)) / side,
            hold.Radius * Math.Max(Width, Height) / side,
            hold.ShapePoints is { Count: >= 3 } shape ? shape.Select(Scaled).ToList() : null,
            hold.ShapePoints is { Count: >= 3 } ? hold.ShapeHoles?.Select(ring => (IReadOnlyList<ShapePoint>)ring.Select(Scaled).ToList()).ToList() : null,
            hold.Color,
            kept,
            hold.Category == Enums.HoldCategory.Foot);
    }

    /// <summary>The spot itself as a plain circle, for a picture that is about no hold (a detection that was left out).</summary>
    /// <returns>The circle.</returns>
    public LookOverOutline Spot() => new(0.5, 0.5, RingPx / (2 * HalfPx), null, null, null, false, false);
}
