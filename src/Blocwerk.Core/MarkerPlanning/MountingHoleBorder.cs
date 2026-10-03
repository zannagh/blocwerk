// <copyright file="MountingHoleBorder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>How a marker's white border (black edge to cut line) compares with <see cref="MountingHoleSafety"/>'s limits.</summary>
public enum MountingHoleBorder
{
    /// <summary>At least <see cref="MountingHoleSafety.RecommendedBorderPx"/>: fine on any wall tone.</summary>
    Clear,

    /// <summary>
    /// Between <see cref="MountingHoleSafety.MinBorderPx"/> and <see cref="MountingHoleSafety.RecommendedBorderPx"/>:
    /// fine on light plywood, but markers on dark holds or volumes lose some photos.
    /// </summary>
    Thin,

    /// <summary>Under <see cref="MountingHoleSafety.MinBorderPx"/>: markers drop out on every wall tone.</summary>
    TooThin,
}
