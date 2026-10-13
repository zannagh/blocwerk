// <copyright file="LookOverPicture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.LookOver;

/// <summary>One picture of a finding and the holds to outline on it.</summary>
/// <param name="View">Which picture: before the update, now, or the 3D texture.</param>
/// <param name="Outlines">
/// The holds on it: the old hold on "before"; the live hold (faded when it is kept, not confirmed) and the new hold on
/// its spot on "now". Empty when there is nothing to outline (the 3D texture, a hold that is gone): a photo then comes
/// with its ring, the 3D texture bare.
/// </param>
public sealed record LookOverPicture(CheckCropView View, IReadOnlyList<LookOverOutline> Outlines);
