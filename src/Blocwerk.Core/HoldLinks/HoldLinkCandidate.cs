// <copyright file="HoldLinkCandidate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldLinks;

/// <summary>A live, placed hold of one panel photo as the link suggestions see it.</summary>
/// <param name="Id">The hold.</param>
/// <param name="PanelId">Its panel photo.</param>
/// <param name="Color">Its colour key, null when not set.</param>
/// <param name="SizeMm">Its measured size (larger of width and height), null when never measured.</param>
/// <param name="IsFoot">Whether it is a foot hold (for the stand-in size).</param>
/// <param name="World">Where it sits in the wall world, mm: on its volume when placed on one, else on its facet.</param>
public sealed record HoldLinkCandidate(Guid Id, Guid PanelId, string? Color, double? SizeMm, bool IsFoot, double[] World);

/// <summary>A suggested pair, unordered and stored with <see cref="HoldAId"/> &lt; <see cref="HoldBId"/>.</summary>
/// <param name="HoldAId">One hold.</param>
/// <param name="HoldBId">The other hold, on another panel.</param>
/// <param name="DistanceMm">How far apart they sit in 3D, mm.</param>
public sealed record HoldLinkPairSuggestion(Guid HoldAId, Guid HoldBId, double DistanceMm)
{
    /// <summary>The pair in stored order.</summary>
    /// <param name="a">One hold.</param>
    /// <param name="b">The other.</param>
    /// <returns>The ordered ids.</returns>
    public static (Guid A, Guid B) Key(Guid a, Guid b) => a.CompareTo(b) < 0 ? (a, b) : (b, a);
}
