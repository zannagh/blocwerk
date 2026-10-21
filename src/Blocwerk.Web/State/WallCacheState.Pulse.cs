// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State;

/// <summary>What job pulses (PR 1.7, <c>PulseWake</c>) read of the circuit's feed subscription.</summary>
public sealed partial class WallCacheState
{
    /// <summary>The circuit's viewer on a live, unrevoked subscription of the wall, or null: read at every pulse delivery.</summary>
    public FeedViewer? ActiveViewerOf(Guid wallId) => link.ActiveViewerOf(wallId);
}
