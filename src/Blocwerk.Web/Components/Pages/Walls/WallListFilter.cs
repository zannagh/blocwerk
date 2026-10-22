// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>The boulder list's search, filters and sort, as <see cref="WallDetail"/> holds them.</summary>
internal sealed record WallListFilter(
    string SearchText,
    string? GradeMin,
    string? GradeMax,
    bool Attempted,
    bool Done,
    bool NotDone,
    bool Favorites,
    bool Created,
    bool Beta,
    bool NeedsReview,
    int Sort);
