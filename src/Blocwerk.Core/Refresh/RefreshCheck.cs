// <copyright file="RefreshCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Refresh;

/// <summary>Which picture of a confirm-screen card: the old photo, the new photo, or this visit's 3D texture.</summary>
public enum CheckCropView
{
    Old = 0,
    New = 1,
    Model = 2,
}

/// <summary>One card of the confirm screen, as the page shows it.</summary>
/// <param name="Id">The card (<see cref="Entities.WallUpdateException"/>).</param>
/// <param name="Kind">Why it needs a look.</param>
/// <param name="Status">Whether, and how, it was answered.</param>
/// <param name="HasOld">There is an old photo of the hold.</param>
/// <param name="HasModel">There is a 3D texture spot for it.</param>
/// <param name="Boulders">Live boulders that use the old hold.</param>
/// <param name="HoldName">The old hold's name or colour, when it has one.</param>
/// <param name="Confidence">The matcher's confidence (low-confidence match).</param>
public sealed record RefreshCheck(
    Guid Id,
    UpdateExceptionKind Kind,
    UpdateExceptionStatus Status,
    bool HasOld,
    bool HasModel,
    int Boulders,
    string? HoldName,
    double? Confidence);
