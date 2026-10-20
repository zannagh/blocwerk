// <copyright file="ReviewSubject.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>What a <see cref="Entities.WallUpdateReviewAnswer"/> is about. Persisted: append new values at the end.</summary>
public enum ReviewSubject
{
    /// <summary>An old hold not found again, kept in place unless a person says otherwise.</summary>
    NotFound = 0,

    /// <summary>A new detection that goes live as a new hold unless a person says otherwise.</summary>
    NewHold = 1,

    /// <summary>A detection the triage left out unless a person adds it.</summary>
    LeftOut = 2,
}
