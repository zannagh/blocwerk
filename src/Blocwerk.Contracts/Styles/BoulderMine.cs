// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

/// <summary>A list of the signed-in viewer's own marks, opened from its card on the browser's landing (<c>?mine=</c>).</summary>
public enum BoulderMine
{
    /// <summary>The boulders the viewer marked as a favourite (<c>?mine=favorites</c>).</summary>
    Favorites,

    /// <summary>The boulders the viewer marked as one of their projects (<c>?mine=projects</c>).</summary>
    Projects,
}
