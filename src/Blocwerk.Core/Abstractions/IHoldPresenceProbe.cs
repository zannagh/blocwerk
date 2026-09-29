// <copyright file="IHoldPresenceProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// One new-photo spot to look for in the old photo (RAW pixels of each photo), with the local scale
/// old pixels per new pixel.
/// </summary>
public readonly record struct PresenceQuery(double NewX, double NewY, double OldX, double OldY, double Scale);

/// <summary>
/// Whether what the new photo shows at a spot was already there in the old photo: the best normalized
/// cross-correlation of the new patch inside a small window of the old photo around the aligned spot.
/// </summary>
public interface IHoldPresenceProbe
{
    /// <summary>Per query, the score in [-1, 1], or null when the patch or window leaves a photo.</summary>
    IReadOnlyList<double?> Score(byte[] oldImage, byte[] newImage, IReadOnlyList<PresenceQuery> queries);
}
