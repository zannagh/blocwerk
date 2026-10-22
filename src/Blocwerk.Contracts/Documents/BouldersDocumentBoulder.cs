// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One boulder of a <see cref="BouldersDocument"/>.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="Grade">Its grade, if set.</param>
/// <param name="Generation">The generation it was set on.</param>
/// <param name="Draft">True for a draft (setters class only); left out otherwise.</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="PublishedAt">When it was published, if it was.</param>
/// <param name="SetBy">The byline: the setters, else the creator, else the placeholder name. Never a user id.</param>
/// <param name="NoMatch">True when matching is not allowed.</param>
/// <param name="KickboardFootholdsOn">True when the kickboard's footholds are on.</param>
/// <param name="HandsFollowFeet">True when hand holds may be used as feet.</param>
/// <param name="FootColorOnly">The one foothold colour allowed, if restricted.</param>
/// <param name="Stats">The wall-wide counts.</param>
/// <param name="Holds">The boulder's holds, by hold id.</param>
public sealed record BouldersDocumentBoulder(
    Guid Id,
    string Name,
    string? Grade,
    int Generation,
    bool? Draft,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string SetBy,
    bool NoMatch,
    bool KickboardFootholdsOn,
    bool HandsFollowFeet,
    string? FootColorOnly,
    BouldersDocumentStats Stats,
    IReadOnlyList<BouldersDocumentHold> Holds);
