// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Microsoft.JSInterop;

namespace Blocwerk.Client.Store;

/// <summary>
/// The device cache through client-store.js (<c>window.bwClientStore</c>, loaded by the shell after the shared cache
/// modules). It answers JSON strings, read here with the source-generated <see cref="ClientJsonContext"/>, so no
/// reflection serializer touches the documents. A missing module or a failed call reads as <c>unavailable</c>.
/// </summary>
public sealed class JsClientStoreSource : IClientStoreSource, IDisposable
{
    private readonly IJSRuntime js;
    private DotNetObjectReference<JsClientStoreSource>? self;
    private Func<Task>? changed;

    /// <summary>Initializes a new instance of the <see cref="JsClientStoreSource"/> class.</summary>
    /// <param name="js">The browser's JS runtime.</param>
    public JsClientStoreSource(IJSRuntime js)
    {
        this.js = js;
    }

    /// <inheritdoc />
    public async Task<StoreSnapshot> SnapshotAsync()
    {
        var json = await CallAsync("bwClientStore.snapshotJson");
        return Parse(json, ClientJsonContext.Default.StoreSnapshot) ?? StoreSnapshot.Nothing("unavailable");
    }

    /// <inheritdoc />
    public async Task<StoreDocs?> DocsAsync(string partition, Guid wallId) =>
        Parse(await CallAsync("bwClientStore.docsJson", partition, wallId.ToString()), ClientJsonContext.Default.StoreDocs);

    /// <inheritdoc />
    public async Task<bool> ClearAsync()
    {
        try
        {
            return await js.InvokeAsync<bool>("bwClientStore.clear");
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ListenAsync(Func<Task> changed)
    {
        this.changed = changed;
        self ??= DotNetObjectReference.Create(this);
        try
        {
            await js.InvokeVoidAsync("bwClientStore.listenDotNet", self);
        }
        catch (JSException)
        {
            // No module: nothing to listen to; the store already reads as unavailable.
        }
    }

    /// <summary>client-store.js's callback: another tab or the identity may have changed what to show.</summary>
    /// <returns>A task that completes after the store's refresh.</returns>
    [JSInvokable]
    public Task OnStoreChanged() => changed?.Invoke() ?? Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => self?.Dispose();

    private static T? Parse<T>(string? json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> CallAsync(string name, params object?[] args)
    {
        try
        {
            return await js.InvokeAsync<string?>(name, args);
        }
        catch (JSException)
        {
            return null;
        }
    }
}
