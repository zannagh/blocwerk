// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;

namespace Blocwerk.Client.Sync;

/// <summary>
/// One wall's reload (plan §1, sync loop step 1): the docs index into the ledger, the documents whose sha changed into
/// the store (each replaces its kind whole), and for a signed-in device the marks. It keeps the build cursor of each
/// document the store holds (<see cref="DocCursors"/>): the replay starts at the oldest, and an event at or before a
/// document's own cursor is already in that document.
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

    /// <summary>Gets the build cursor of each document kind the store holds (unknown kinds are absent).</summary>
    public IReadOnlyDictionary<string, string> DocCursors { get; private set; } = new Dictionary<string, string>();

    /// <summary>The index, the changed documents and the marks.</summary>
    /// <returns>
    /// Whether the wall is still this device's to sync; whether the documents arrived (the index answered and every
    /// changed document came); whether the marks did; and the replay cursor (null: open with a resync).
    /// </returns>
    public async Task<(bool Keep, bool Reached, bool Marks, string? Cursor)> RunAsync()
    {
        var index = await transport.RefreshIndexAsync(partition, wallId, token);
        if (!await KeepAsync(index))
        {
            return (false, false, false, null);
        }

        // Unreachable or a server error: the documents the ledger names stand, and so do their cursors.
        var docs = index.Docs ?? store.Walls.FirstOrDefault(w => w.WallId == wallId)?.Data ?? new Dictionary<string, StoreDocRef>();
        var fetched = await ReplaceChangedAsync(docs);
        var marks = await MineAsync();
        var cursor = await ReplayCursorAsync(docs);
        return (true, index.Docs is not null && fetched, marks, cursor);
    }

    /// <summary>The docs index and the changed documents only (a structural kind's debounced refetch).</summary>
    /// <returns>Whether the wall is still this device's to sync, and whether everything arrived.</returns>
    public async Task<(bool Keep, bool Reached)> IndexAsync()
    {
        var index = await transport.RefreshIndexAsync(partition, wallId, token);
        if (!await KeepAsync(index))
        {
            return (false, false);
        }

        if (index.Docs is not { } docs)
        {
            return (true, false);
        }

        var fetched = await ReplaceChangedAsync(docs);
        await ReplayCursorAsync(docs);
        return (true, fetched);
    }

    /// <summary>A refusal of the wall's feed: the identity matrix decides (drop, lock or wipe).</summary>
    /// <param name="status">The status.</param>
    /// <param name="reason">The sign-out reason, or null.</param>
    /// <returns>A task that completes once applied.</returns>
    public Task RefusedAsync(int status, string? reason) => refusals.RefusedAsync(partition, wallId, status, reason, mine: false);

    /// <summary>The viewer's marks (signed-in only).</summary>
    /// <returns>True when the marks are current (or the device holds none).</returns>
    public Task<bool> MineAsync() => MineRefresh.RunAsync(store, transport, refusals, partition, wallId);

    private async Task<bool> KeepAsync(SyncIndex index)
    {
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

        return true;
    }

    // True when every changed document arrived.
    private async Task<bool> ReplaceChangedAsync(IReadOnlyDictionary<string, StoreDocRef> docs)
    {
        var all = true;
        foreach (var (kind, doc) in docs)
        {
            var wall = await store.GetWallAsync(wallId);
            if (wall is null)
            {
                return false;
            }

            if (wall.Shas.TryGetValue(kind, out var held) && held == doc.Sha)
            {
                continue;
            }

            if (await transport.FetchDocumentAsync(wallId, kind, doc.Sha, token) is { } json)
            {
                store.ReplaceDocument(partition, wallId, kind, doc.Sha, json);
            }
            else
            {
                all = false;
            }
        }

        return all;
    }

    // A kind whose new document did not arrive is still the older one, whose cursor is unknown: no cursor then, and the
    // server opens the stream with a resync. A kind the device never held does not hold the replay back.
    private async Task<string?> ReplayCursorAsync(IReadOnlyDictionary<string, StoreDocRef> docs)
    {
        var wall = await store.GetWallAsync(wallId);
        if (wall is null)
        {
            DocCursors = new Dictionary<string, string>();
            return null;
        }

        var known = new Dictionary<string, string>();
        var cursors = new List<string?>();
        foreach (var (kind, held) in wall.Shas)
        {
            var cursor = docs.TryGetValue(kind, out var doc) && doc.Sha == held ? doc.Cursor : null;
            cursors.Add(cursor);
            if (cursor is not null)
            {
                known[kind] = cursor;
            }
        }

        DocCursors = known;
        return SyncCursor.Earliest(cursors);
    }
}
