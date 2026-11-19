// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

/// <summary>Which screen of the boulder browser a URL opens.</summary>
public enum BoulderBrowseLevel
{
    /// <summary>The cards: All, By Hold Type, By Style and (signed in) the viewer's own lists.</summary>
    Landing,

    /// <summary>One card per hold type (<c>?browse=holds</c>).</summary>
    HoldTypes,

    /// <summary>One card per style (<c>?browse=styles</c>).</summary>
    Styles,

    /// <summary>The list: every boulder (<c>?style=all</c>), one style's, one hold type's or the viewer's favourites.</summary>
    List,
}
