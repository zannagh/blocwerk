// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>Whom this circuit subscribes to a wall's feed as.</summary>
public interface ICircuitFeedViewer
{
    /// <summary>The circuit's viewer on <paramref name="wallId"/>, read fresh; null when it has no access.</summary>
    Task<FeedViewer?> ResolveAsync(Guid wallId);
}
