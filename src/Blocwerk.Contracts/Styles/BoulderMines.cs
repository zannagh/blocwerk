// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

/// <summary>The URL names of the viewer's own lists (<see cref="BoulderMine"/>).</summary>
public static class BoulderMines
{
    /// <summary>The name a list goes by in the URL.</summary>
    /// <param name="mine">The list.</param>
    /// <returns>Its name, lower case.</returns>
    public static string Slug(BoulderMine mine) => mine switch
    {
        BoulderMine.Favorites => "favorites",
        BoulderMine.Projects => "projects",
        _ => mine.ToString().ToLowerInvariant(),
    };

    /// <summary>Reads a list from its URL name, ignoring case and the British spelling of favourites.</summary>
    /// <param name="value">The <c>mine</c> query value.</param>
    /// <returns>The list, or null for an empty or unknown value.</returns>
    public static BoulderMine? FromSlug(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "favorites" or "favourites" => BoulderMine.Favorites,
        "projects" => BoulderMine.Projects,
        _ => null,
    };
}
