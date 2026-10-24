// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.JSInterop;

namespace Blocwerk.Client.Views;

/// <summary>
/// The view facts through client-view.js (<c>window.bwClientView</c>), read once per start: a rotation keeps the rung
/// the page began with, and a miss on it falls back to the thumbnail (<see cref="PhotoUrls"/>).
/// </summary>
public sealed class JsClientViewEnv : IClientViewEnv
{
    private readonly IJSRuntime js;
    private Task<ClientViewFacts>? facts;

    /// <summary>Initializes a new instance of the <see cref="JsClientViewEnv"/> class.</summary>
    /// <param name="js">The browser's JS runtime.</param>
    public JsClientViewEnv(IJSRuntime js)
    {
        this.js = js;
    }

    /// <inheritdoc />
    public Task<ClientViewFacts> FactsAsync() => facts ??= ReadAsync();

    private async Task<ClientViewFacts> ReadAsync()
    {
        try
        {
            var width = await js.InvokeAsync<int>("bwClientView.fitWidth");
            var offset = await js.InvokeAsync<int>("bwClientView.tzOffset");
            return new ClientViewFacts(width > 0 ? width : ClientViewFacts.Default.FitWidth, TimeSpan.FromMinutes(Math.Clamp(offset, -14 * 60, 14 * 60)));
        }
        catch (JSException)
        {
            return ClientViewFacts.Default;
        }
    }
}
