// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Client.Store;
using Blocwerk.Client.Sync;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Tests.OfflineClientSync;

/// <summary>
/// The server and the device for the sync loop's tests: queued answers per route (a default when none is queued), the
/// streams it opened, the refusals it handed on, the bubbles it showed. Every call is logged in <see cref="Calls"/>.
/// </summary>
internal sealed class FakeSyncTransport : ISyncTransport
{
    public List<string> Calls { get; } = [];

    /// <summary>Gets or sets the docs the index names by default (an <c>unchanged</c> refresh).</summary>
    public Dictionary<string, StoreDocRef> IndexDocs { get; set; } = [];

    public Queue<SyncIndex> Indexes { get; } = new();

    public Dictionary<string, string> Documents { get; } = [];

    public Queue<SyncResponse> Mines { get; } = new();

    public Queue<SyncResponse> Changes { get; } = new();

    public Queue<SyncResponse> MyChanges { get; } = new();

    public List<FakeSyncStream> Streams { get; } = [];

    public List<(string Partition, Guid WallId, int Status, string? Reason, bool Mine)> Refusals { get; } = [];

    /// <summary>Runs on each refusal, before it answers (the device wiping or locking, say).</summary>
    public Action<int, string?>? OnRefused { get; set; }

    public List<string> Bubbles { get; } = [];

    /// <summary>Gets or sets a value indicating whether each stream's 200 arrives (false: a cap's hidden 429).</summary>
    public bool StreamsOpen { get; set; } = true;

    /// <summary>Gets or sets the label the cookie names after an own-rows page (another user signed in, when it differs).</summary>
    public string? MePartition { get; set; }

    /// <summary>Gets the stored resume cursor of each wall (the ledger rows' feedCursor).</summary>
    public Dictionary<(string Partition, Guid WallId), string> WallCursors { get; } = [];

    /// <summary>Gets the own-rows poll's stored cursor per label.</summary>
    public Dictionary<string, string> MeCursors { get; } = [];

    public ISyncSignals? Signals { get; private set; }

    public bool Visible { get; set; } = true;

    public FakeSyncStream Stream => Streams[^1];

    public static SyncResponse Page(string next, bool more = false, bool resync = false, params FeedEnvelope[] events) =>
        new(200, Body: JsonSerializer.Serialize(new FeedChanges(events, next, resync, more), FeedJson.Options));

    public Task<SyncIndex> RefreshIndexAsync(string partition, Guid wallId, string? token)
    {
        Calls.Add($"index {wallId} {token}");
        return Task.FromResult(Indexes.TryDequeue(out var index) ? index : new SyncIndex(304, "unchanged", null, IndexDocs));
    }

    public Task<string?> FetchDocumentAsync(Guid wallId, string kind, string sha, string? token)
    {
        Calls.Add($"doc {kind} {sha[..4]}");
        return Task.FromResult(Documents.GetValueOrDefault(sha));
    }

    public Task<SyncResponse> RefreshMineAsync(string partition, Guid wallId)
    {
        Calls.Add($"mine {wallId}");
        return Task.FromResult(Mines.TryDequeue(out var mine) ? mine : SyncWorld.Mine(partition));
    }

    public Task<SyncResponse> ChangesAsync(Guid wallId, string? after, string? token)
    {
        Calls.Add($"changes {after}");
        return Task.FromResult(Changes.TryDequeue(out var page) ? page : Page(after ?? "1.0.0"));
    }

    public Task<SyncResponse> MyChangesAsync(string? after)
    {
        Calls.Add($"me {after}");
        var page = MyChanges.TryDequeue(out var queued) ? queued : Page(after ?? "1.0.0");
        return Task.FromResult(page.Status == 200 && page.Partition is null ? page with { Partition = MePartition } : page);
    }

    public Task<string?> SavedCursorAsync(string partition, Guid wallId) => Task.FromResult(WallCursors.GetValueOrDefault((partition, wallId)));

    public Task SaveCursorAsync(string partition, Guid wallId, string cursor)
    {
        WallCursors[(partition, wallId)] = cursor;
        return Task.CompletedTask;
    }

    public Task<string?> SavedMeCursorAsync(string partition) => Task.FromResult(MeCursors.GetValueOrDefault(partition));

    public Task SaveMeCursorAsync(string partition, string cursor)
    {
        MeCursors[partition] = cursor;
        return Task.CompletedTask;
    }

    public IDisposable OpenStream(Guid wallId, string? after, string? token, ISyncStreamSink sink)
    {
        Calls.Add($"open {after}");
        var stream = new FakeSyncStream(wallId, after, token, sink);
        Streams.Add(stream);
        if (StreamsOpen)
        {
            _ = sink.OnMessageAsync("open", null);
        }

        return stream;
    }

    public Task<string> RefusedAsync(string partition, Guid wallId, int status, string? reason, bool mine)
    {
        Calls.Add($"refused {status} {reason}");
        Refusals.Add((partition, wallId, status, reason, mine));
        OnRefused?.Invoke(status, reason);
        return Task.FromResult("dropped");
    }

    public Task<bool> WatchAsync(ISyncSignals signals)
    {
        Signals = signals;
        return Task.FromResult(Visible);
    }

    public void ShowStatus(string kind) => Bubbles.Add(kind);

    /// <summary>The calls of one route (their prefix), in order.</summary>
    public List<string> Of(string route) => Calls.Where(c => c.StartsWith(route + " ", StringComparison.Ordinal) || c == route).ToList();
}
