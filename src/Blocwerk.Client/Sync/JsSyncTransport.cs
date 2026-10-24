// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Microsoft.JSInterop;

namespace Blocwerk.Client.Sync;

/// <summary>
/// The sync loop's server and device through client-sync.js (<c>window.bwClientSync</c>, loaded by the shell after the
/// cache modules and Web's asset-manifest/data/mine.js). Answers are JSON strings read with
/// <see cref="SyncJsonContext"/>; a missing module or a failed call is a status of 0.
/// </summary>
public sealed class JsSyncTransport : ISyncTransport
{
    private readonly IJSRuntime js;
    private DotNetObjectReference<JsSyncSignals>? signals;

    /// <summary>Initializes a new instance of the <see cref="JsSyncTransport"/> class.</summary>
    /// <param name="js">The browser's JS runtime.</param>
    public JsSyncTransport(IJSRuntime js)
    {
        this.js = js;
    }

    /// <inheritdoc />
    public async Task<SyncIndex> RefreshIndexAsync(string partition, Guid wallId, string? token) =>
        SyncJson.Read(await CallAsync("bwClientSync.indexJson", partition, Id(wallId), token), SyncJsonContext.Default.SyncIndex)
        ?? new SyncIndex(0, "offline");

    /// <inheritdoc />
    public Task<string?> FetchDocumentAsync(Guid wallId, string kind, string sha, string? token) =>
        CallAsync("bwClientSync.doc", Id(wallId), kind, sha, token);

    /// <inheritdoc />
    public async Task<SyncResponse> RefreshMineAsync(string partition, Guid wallId) =>
        Response(await CallAsync("bwClientSync.mineJson", partition, Id(wallId)));

    /// <inheritdoc />
    public async Task<SyncResponse> ChangesAsync(Guid wallId, string? after, string? token) =>
        Response(await CallAsync("bwClientSync.changesJson", Id(wallId), after, token));

    /// <inheritdoc />
    public async Task<SyncResponse> MyChangesAsync(string? after) =>
        Response(await CallAsync("bwClientSync.meChangesJson", after));

    /// <inheritdoc />
    public IDisposable OpenStream(Guid wallId, string? after, string? token, ISyncStreamSink sink)
    {
        var stream = new JsSyncStream(js, sink);
        stream.Open(Id(wallId), after, token);
        return stream;
    }

    /// <inheritdoc />
    public async Task<string> RefusedAsync(string partition, Guid wallId, int status, string? reason, bool mine) =>
        await CallAsync("bwClientSync.refused", partition, Id(wallId), status, reason, mine) ?? "kept";

    /// <inheritdoc />
    public async Task<bool> WatchAsync(ISyncSignals signals)
    {
        this.signals ??= DotNetObjectReference.Create(new JsSyncSignals(signals));
        try
        {
            return await js.InvokeAsync<bool>("bwClientSync.watch", this.signals);
        }
        catch (JSException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public void ShowStatus(string kind) => _ = CallAsync("bwClientSync.status", kind);

    private static string Id(Guid wallId) => wallId.ToString("D", CultureInfo.InvariantCulture);

    private static SyncResponse Response(string? json) =>
        SyncJson.Read(json, SyncJsonContext.Default.SyncResponse) ?? new SyncResponse(0);

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
