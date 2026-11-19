// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;

namespace Blocwerk.Shared.Ui.Styles;

/// <summary>The boulder browser's own icons and names (the landing cards'); a style's or hold type's come from its definition.</summary>
public static class BrowseIcons
{
    /// <summary>Four tiles: every boulder.</summary>
    public const string All =
        "<rect x=\"3\" y=\"3\" width=\"7\" height=\"7\" rx=\"1\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"7\" rx=\"1\"/>"
        + "<rect x=\"3\" y=\"14\" width=\"7\" height=\"7\" rx=\"1\"/><rect x=\"14\" y=\"14\" width=\"7\" height=\"7\" rx=\"1\"/>";

    /// <summary>An open hand: by hold type.</summary>
    public const string HoldTypes =
        "<path d=\"M8 13V5a1.5 1.5 0 0 1 3 0v6\"/><path d=\"M11 11V4a1.5 1.5 0 0 1 3 0v7\"/>"
        + "<path d=\"M14 11V5.5a1.5 1.5 0 0 1 3 0V14a7 7 0 0 1-7 7h-.5a6 6 0 0 1-5-2.7L3 15.5a1.5 1.5 0 0 1 2.4-1.8L8 16\"/>";

    /// <summary>Sparkles: by style.</summary>
    public const string Styles =
        "<path d=\"M12 3l1.8 4.7 4.7 1.8-4.7 1.8L12 16l-1.8-4.7-4.7-1.8 4.7-1.8z\"/>"
        + "<path d=\"M19 15l.8 2.2 2.2.8-2.2.8L19 21l-.8-2.2-2.2-.8 2.2-.8z\"/>";

    /// <summary>A heart: the viewer's favourites.</summary>
    public const string Favorites =
        "<path d=\"M19.5 12.6 12 20l-7.5-7.4A4.8 4.8 0 0 1 12 6.3a4.8 4.8 0 0 1 7.5 6.3z\"/>";

    /// <summary>A flag on a summit: the viewer's projects.</summary>
    public const string Projects =
        "<path d=\"M3 21l6.5-11 3.5 5.5 2.5-3.5L21 21z\"/><path d=\"M13 15V3l5 2.5L13 8\"/>";

    /// <summary>The name and icon of a screen or list, for the bar above it.</summary>
    /// <param name="browse">The level.</param>
    /// <returns>Its name and icon.</returns>
    public static (string Label, string Icon) Describe(BoulderBrowse browse) => browse switch
    {
        { Level: BoulderBrowseLevel.HoldTypes } => ("By Hold Type", HoldTypes),
        { Level: BoulderBrowseLevel.Styles } => ("By Style", Styles),
        { HoldType: { } hold } => (BoulderHoldTypes.Label(hold), BoulderHoldTypes.Icon(hold)),
        { Style: { } style } => (BoulderStyles.Label(style), BoulderStyles.Icon(style)),
        { Mine: BoulderMine.Favorites } => ("Favorites", Favorites),
        { Mine: BoulderMine.Projects } => ("My projects", Projects),
        _ => ("All", All),
    };
}
