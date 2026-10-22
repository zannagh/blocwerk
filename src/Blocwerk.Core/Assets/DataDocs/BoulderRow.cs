// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>The columns <see cref="BouldersDocumentBuilder"/> reads of a boulder and its creator (no user row, no avatar bytes).</summary>
internal sealed record BoulderRow(
    Guid Id,
    string Name,
    string? Grade,
    int Generation,
    bool IsDraft,
    bool IsHistoric,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    bool NoMatch,
    bool KickboardFootholdsOn,
    bool HandsFollowFeet,
    string? FootColorOnly,
    Guid CreatorId,
    string CreatorDisplayName,
    string? CreatorCustomName)
{
    /// <summary>The creator as <c>BoulderSetterNames.Describe</c> reads one: id and names only.</summary>
    public User Creator() => new()
    {
        Id = CreatorId,
        Identifier = string.Empty,
        DisplayName = CreatorDisplayName,
        CustomDisplayName = CreatorCustomName,
    };
}
