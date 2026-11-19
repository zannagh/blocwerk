// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Shared.Ui.Styles;

/// <summary>How a card of the boulder browser is drawn (BoulderBrowser.razor names each one's CSS class).</summary>
public enum BrowseCardShape
{
    /// <summary>A card page's tile: icon beside name and count.</summary>
    Tile,

    /// <summary>The landing's All card, across the grid, content centred.</summary>
    Hero,

    /// <summary>One of the landing's pair (By Hold Type, By Style): taller than wide on a phone.</summary>
    Tall,

    /// <summary>One of the viewer's own lists on the landing.</summary>
    Mine,
}
