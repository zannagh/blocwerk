// <copyright file="HoldLinkSuggestionStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>Where a <see cref="Entities.HoldLinkSuggestion"/> stands. A linked pair is not kept: the link itself says it.</summary>
public enum HoldLinkSuggestionStatus
{
    /// <summary>Waiting for a wall admin.</summary>
    Pending = 0,

    /// <summary>The admin said the two holds are not the same; the pair is never suggested again.</summary>
    Rejected = 1,
}
