// <copyright file="LookOverFilter.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>Which open findings the look-over's card deck asks about: all of them, those on a boulder's hold, or one group.</summary>
/// <param name="OnBouldersOnly">Only findings on a hold a boulder uses.</param>
/// <param name="MissingOnly">Only the holds the update could not find.</param>
/// <param name="Kind">Only the cards of this kind.</param>
/// <param name="DuplicatesOnly">
/// Only the discarded detections a hold on the wall covers. They are in no other queue: probably second detections of
/// holds that are there, kept out of the way behind their own chip.
/// </param>
public sealed record LookOverFilter(bool OnBouldersOnly = false, bool MissingOnly = false, LookOverFindingKind? Kind = null, bool DuplicatesOnly = false)
{
    /// <summary>Everything that is open.</summary>
    public static readonly LookOverFilter All = new();

    /// <summary>What is open on a hold a boulder uses.</summary>
    public static readonly LookOverFilter OnBoulders = new(OnBouldersOnly: true);

    /// <summary>The holds the update could not find.</summary>
    public static readonly LookOverFilter Missing = new(MissingOnly: true);

    /// <summary>The discarded detections that are probably duplicates of a hold on the wall.</summary>
    public static readonly LookOverFilter Duplicates = new(DuplicatesOnly: true);
}
