// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>The kinds of <see cref="WallFeedAccessAction"/>.</summary>
public enum WallFeedAccessKind
{
    /// <summary>Nothing about the circuit's access changed.</summary>
    None,

    /// <summary>Removed from the wall: leave it.</summary>
    Leave,

    /// <summary>Demoted: subscribe again with the named role's audiences.</summary>
    Demote,

    /// <summary>Possibly promoted, re-added or made owner: read the viewer from the database again.</summary>
    Reread,
}
