// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State;

/// <summary>What a <see cref="WallCacheChange"/> refers to.</summary>
public enum WallCacheChangeScope
{
    /// <summary>Wall-level entries went (the aggregate, a hold set, or all of the wall).</summary>
    Wall,

    /// <summary>One boulder went. Its wall's aggregate may still be cached.</summary>
    Boulder,

    /// <summary>The circuit's wall list went: walls were created, deleted, joined or left, or a listed wall changed.</summary>
    WallList,
}
