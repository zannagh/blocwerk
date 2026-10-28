// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>What the sync loop (PR 3.12) still has to do after the store applied a feed event.</summary>
[Flags]
public enum FeedFollowUp
{
    /// <summary>Nothing: the event was ignored or fully applied.</summary>
    None = 0,

    /// <summary>Refetch the wall's docs index after the 10 s debounce (a structural kind, the comment kinds included).</summary>
    RefetchIndex = 1,

    /// <summary>Refetch the viewer's marks (signed-in only).</summary>
    RefetchMine = 2,

    /// <summary>Refetch everything (a resync, or an envelope version the client does not know).</summary>
    Resync = 4,
}
