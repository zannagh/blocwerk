// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One boulder of a <see cref="BouldersDocument"/>.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="Grade">Its grade, if set.</param>
/// <param name="Generation">The generation it was set on.</param>
/// <param name="Draft">True for a draft (members and setters only); left out otherwise.</param>
/// <param name="IsHistoric">True for a boulder of an earlier generation, listed with a "historic" badge.</param>
/// <param name="IsArchived">True for an archived boulder (members and setters only), listed under "Archive".</param>
/// <param name="CreatedAt">When it was created.</param>
/// <param name="PublishedAt">When it was published, if it was.</param>
/// <param name="SetBy">The byline: the setters, else the creator, else the placeholder name. Never a user id.</param>
/// <param name="NoMatch">True when matching is not allowed.</param>
/// <param name="KickboardFootholdsOn">True when the kickboard's footholds are on.</param>
/// <param name="HandsFollowFeet">True when hand holds may be used as feet.</param>
/// <param name="FootColorOnly">The one foothold colour allowed, if restricted.</param>
/// <param name="Stats">The wall-wide counts.</param>
/// <param name="Holds">The boulder's holds, by hold id.</param>
/// <param name="CustomRule">The setter's free-text rule (plain text), if any.</param>
/// <param name="Styles">The style tags (<c>Tension</c>, <c>Power</c>...) it carries; left out when it has none. Appended
/// last and optional, so an older client reads the document unchanged and a document without it means "no tags".</param>
/// <param name="ParentBoulderId">The boulder this one is a variant of, if any; left out otherwise, so a client that does not know it reads the document unchanged.</param>
/// <param name="HoldType">The dominant hold type set on the boulder, by name (<c>Crimp</c>...); left out when none is set and
/// its holds decide. Appended last and optional, like <paramref name="Styles"/>.</param>
public sealed record BouldersDocumentBoulder(
    Guid Id,
    string Name,
    string? Grade,
    int Generation,
    bool? Draft,
    bool IsHistoric,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt,
    string SetBy,
    bool NoMatch,
    bool KickboardFootholdsOn,
    bool HandsFollowFeet,
    string? FootColorOnly,
    BouldersDocumentStats Stats,
    IReadOnlyList<BouldersDocumentHold> Holds,
    string? CustomRule = null,
    IReadOnlyList<string>? Styles = null,
    Guid? ParentBoulderId = null,
    string? HoldType = null);
