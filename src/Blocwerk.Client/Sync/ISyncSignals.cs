// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>The page's signals the sync loop follows: the tab hidden or shown, the browser back online.</summary>
public interface ISyncSignals
{
    /// <summary>The tab was hidden or shown (<c>visibilitychange</c>).</summary>
    /// <param name="visible">True when it is visible now.</param>
    /// <returns>A task that completes once handled.</returns>
    Task OnVisibilityAsync(bool visible);

    /// <summary>The browser came back <c>online</c>.</summary>
    /// <returns>A task that completes once handled.</returns>
    Task OnOnlineAsync();
}
