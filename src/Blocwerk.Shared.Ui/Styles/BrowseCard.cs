// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Shared.Ui.Styles;

/// <summary>One card of the boulder browser.</summary>
/// <param name="Key">What it stands for (<c>all</c>, <c>holds</c>, <c>crimp</c>...), for tests and styling.</param>
/// <param name="Label">Its name.</param>
/// <param name="Icon">Its icon's SVG content.</param>
/// <param name="Count">How many families it leads to; a card with none is greyed out and leads nowhere.</param>
/// <param name="Href">Where a tap goes.</param>
/// <param name="Shape">How it is drawn: a card page's tile, or one of the landing's shapes.</param>
/// <param name="Description">What it stands for, as its tooltip; empty for none.</param>
public sealed record BrowseCard(
    string Key, string Label, string Icon, int Count, string Href, BrowseCardShape Shape = BrowseCardShape.Tile, string Description = "")
{
    /// <summary>Gets the tooltip: the description, or null so a card without one has no title attribute.</summary>
    public string? Tooltip => string.IsNullOrEmpty(Description) ? null : Description;
}
