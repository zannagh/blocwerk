// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.JSInterop;

namespace Blocwerk.Client.Sync;

/// <summary>
/// One wall's <c>EventSource</c> in client-sync.js (<c>bwClientSync.open</c>): its messages come back through
/// <see cref="OnMessage"/> in the order the browser received them. A bye or an error closes it on the JS side, so the
/// browser never reconnects on its own: the sync loop probes and reopens.
/// </summary>
public sealed class JsSyncStream : IDisposable
{
    private readonly IJSRuntime js;
    private readonly ISyncStreamSink sink;
    private readonly DotNetObjectReference<JsSyncStream> self;
    private Task<int>? id;
    private bool disposed;

    /// <summary>Initializes a new instance of the <see cref="JsSyncStream"/> class.</summary>
    /// <param name="js">The browser's JS runtime.</param>
    /// <param name="sink">Hears the stream.</param>
    public JsSyncStream(IJSRuntime js, ISyncStreamSink sink)
    {
        this.js = js;
        this.sink = sink;
        self = DotNetObjectReference.Create(this);
    }

    /// <summary>A stream message (client-sync.js's callback).</summary>
    /// <param name="type">message, resync, bye or error.</param>
    /// <param name="data">Its JSON, or null.</param>
    /// <returns>A task that completes once the loop took it in.</returns>
    [JSInvokable]
    public Task OnMessage(string type, string? data) => disposed ? Task.CompletedTask : sink.OnMessageAsync(type, data);

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        _ = CloseAsync();
    }

    internal void Open(string wallId, string? after, string? token) => id = OpenAsync(wallId, after, token);

    private async Task<int> OpenAsync(string wallId, string? after, string? token)
    {
        try
        {
            return await js.InvokeAsync<int>("bwClientSync.open", wallId, after, token, self);
        }
        catch (JSException)
        {
            _ = sink.OnMessageAsync("error", null);
            return 0;
        }
    }

    private async Task CloseAsync()
    {
        try
        {
            if (id is not null && await id is var opened and > 0)
            {
                await js.InvokeVoidAsync("bwClientSync.close", opened);
            }
        }
        catch (JSException)
        {
            // Already gone.
        }
        finally
        {
            self.Dispose();
        }
    }
}
