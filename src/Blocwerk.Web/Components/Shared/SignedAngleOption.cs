// <copyright file="SignedAngleOption.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>One direction of a <see cref="SignedAngleInput"/>: "Slab" (−), "Vertical" (0), "Overhang" (+).</summary>
/// <param name="Name">The button / option text.</param>
/// <param name="Sign">−1, 0 or +1: the sign this direction stores the angle with.</param>
/// <param name="DefaultMagnitude">
/// The size to start from when switching here from zero; null waits for a typed size instead of
/// inventing one (for optional, "as you know it" angles).
/// </param>
/// <param name="AngleLabel">The size field's label; defaults to "{Name} angle (°)".</param>
public sealed record SignedAngleOption(string Name, int Sign, double? DefaultMagnitude = null, string? AngleLabel = null);
