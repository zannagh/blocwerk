// <copyright file="IHoldPresenceProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// One new-photo spot to look for in the old photo (RAW pixels of each photo), with the local scale
/// old pixels per new pixel. <paramref name="RadiusPx"/> is the radius on the new photo of the hold asked about, when the
/// question is about a hold: the compared patch and the search window then grow with it (<see cref="PresencePatch"/>).
/// 0 (the default) keeps the fixed sizes.
/// </summary>
public readonly record struct PresenceQuery(double NewX, double NewY, double OldX, double OldY, double Scale, double RadiusPx = 0);

/// <summary>
/// Whether what the new photo shows at a spot was already there in the old photo: the best normalized
/// cross-correlation of the new patch inside a small window of the old photo around the aligned spot.
/// </summary>
public interface IHoldPresenceProbe
{
    /// <summary>Per query, the score in [-1, 1], or null when too little of either photo surrounds the spot.</summary>
    IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries);
}
