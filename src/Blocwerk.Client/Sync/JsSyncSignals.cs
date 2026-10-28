// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.JSInterop;

namespace Blocwerk.Client.Sync;

/// <summary>client-sync.js's <c>watch</c> callbacks: <c>visibilitychange</c> and <c>online</c>.</summary>
public sealed class JsSyncSignals
{
    private readonly ISyncSignals signals;

    /// <summary>Initializes a new instance of the <see cref="JsSyncSignals"/> class.</summary>
    /// <param name="signals">Hears them.</param>
    public JsSyncSignals(ISyncSignals signals)
    {
        this.signals = signals;
    }

    /// <summary>The tab was hidden or shown.</summary>
    /// <param name="visible">True when visible now.</param>
    /// <returns>A task that completes once handled.</returns>
    [JSInvokable]
    public Task OnVisibility(bool visible) => signals.OnVisibilityAsync(visible);

    /// <summary>The browser came back online.</summary>
    /// <returns>A task that completes once handled.</returns>
    [JSInvokable]
    public Task OnOnline() => signals.OnOnlineAsync();
}
