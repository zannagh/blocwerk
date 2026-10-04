// <copyright file="UpdateExceptionKind.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Why a hold of a panel update needs a look on the confirm screen (<see cref="Entities.WallUpdateException"/>).</summary>
public enum UpdateExceptionKind
{
    /// <summary>An old hold not found again, where both the new photo and this visit's 3D model show bare wall.</summary>
    PossiblyRemoved = 0,

    /// <summary>An old hold carried onto a new detection the matcher was unsure about.</summary>
    LowConfidenceMatch = 1,

    /// <summary>A new detection the photo check leaves out, while this visit's 3D model sees a hold there.</summary>
    ConflictingNew = 2,
}
