// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;

namespace Blocwerk.Client.Sync;

/// <summary>
/// One wall's reload (plan §1, sync loop step 1): the docs index into the ledger, the documents whose sha changed into
/// the store (each replaces its kind whole), and for a signed-in device the marks. It answers the cursor to replay the
/// feed from: the oldest build cursor of the documents the store now holds.
/// </summary>
internal sealed class WallRefresh
{
    private readonly ClientStore store;
    private readonly ISyncTransport transport;
    private readonly ISyncRefusals refusals;
    private readonly string partition;
    private readonly Guid wallId;
    private readonly string? token;

    public WallRefresh(ClientStore store, ISyncTransport transport, ISyncRefusals refusals, string partition, Guid wallId, string? token)
    {
        this.store = store;
        this.transport = transport;
        this.refusals = refusals;
        this.partition = partition;
        this.wallId = wallId;
        this.token = token;
    }

    /// <summary>The index, the changed documents and the marks.</summary>
    /// <returns>Whether the wall is still this device's to sync, and the replay cursor (null: open with a resync).</returns>
    public async Task<(bool Keep, string? Cursor)> RunAsync()
    {
        var index = await transport.RefreshIndexAsync(partition, wallId, token);
        if (SyncLabels.IsRefusal(index.Status))
        {
            await refusals.RefusedAsync(partition, wallId, index.Status, index.Reason, mine: false);
            return (false, null);
        }

        if (index.Gone)
        {
            await store.RefreshAsync();
            return (false, null);
        }

        // Unreachable or a server error: the documents the ledger names stand, and so do their cursors.
        var docs = index.Docs ?? store.Walls.FirstOrDefault(w => w.WallId == wallId)?.Data ?? new Dictionary<string, StoreDocRef>();
        await ReplaceChangedAsync(docs);
        await MineAsync();
        return (true, await ReplayCursorAsync(docs));
    }

    /// <summary>The docs index and the changed documents only (a structural kind's debounced refetch).</summary>
    /// <returns>Whether the wall is still this device's to sync.</returns>
    public async Task<bool> IndexAsync()
    {
        var index = await transport.RefreshIndexAsync(partition, wallId, token);
        if (SyncLabels.IsRefusal(index.Status))
        {
            await refusals.RefusedAsync(partition, wallId, index.Status, index.Reason, mine: false);
            return false;
        }

        if (index.Gone)
        {
            await store.RefreshAsync();
            return false;
        }

        if (index.Docs is { } docs)
        {
            await ReplaceChangedAsync(docs);
        }

        return true;
    }

    /// <summary>A refusal of the wall's feed: the identity matrix decides (drop, lock or wipe).</summary>
    /// <param name="status">The status.</param>
    /// <param name="reason">The sign-out reason, or null.</param>
    /// <returns>A task that completes once applied.</returns>
    public Task RefusedAsync(int status, string? reason) => refusals.RefusedAsync(partition, wallId, status, reason, mine: false);

    /// <summary>The viewer's marks (signed-in only).</summary>
    /// <returns>A task that completes once refetched.</returns>
    public Task MineAsync() => MineRefresh.RunAsync(store, transport, refusals, partition, wallId);

    private async Task ReplaceChangedAsync(IReadOnlyDictionary<string, StoreDocRef> docs)
    {
        foreach (var (kind, doc) in docs)
        {
            var wall = await store.GetWallAsync(wallId);
            if (wall is null)
            {
                return;
            }

            if (wall.Shas.TryGetValue(kind, out var held) && held == doc.Sha)
            {
                continue;
            }

            if (await transport.FetchDocumentAsync(wallId, kind, doc.Sha, token) is { } json)
            {
                store.ReplaceDocument(partition, wallId, kind, doc.Sha, json);
            }
        }
    }

    // A kind whose new document did not arrive is still the older one, whose cursor is unknown: no cursor then, and the
    // server opens the stream with a resync. A kind the device never held does not hold the replay back.
    private async Task<string?> ReplayCursorAsync(IReadOnlyDictionary<string, StoreDocRef> docs)
    {
        var wall = await store.GetWallAsync(wallId);
        if (wall is null)
        {
            return null;
        }

        var cursors = new List<string?>();
        foreach (var (kind, held) in wall.Shas)
        {
            cursors.Add(docs.TryGetValue(kind, out var doc) && doc.Sha == held ? doc.Cursor : null);
        }

        return SyncCursor.Earliest(cursors);
    }
}
