// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;

namespace Blocwerk.Core.Tests.OfflineClientStore;

/// <summary>A device cache for <see cref="ClientStore"/>'s tests: a snapshot, documents by (partition, wall), a clear.</summary>
internal sealed class FakeStoreSource : IClientStoreSource
{
    public StoreSnapshot Snapshot { get; set; } = StoreSnapshot.Nothing("empty");

    public Dictionary<(string Partition, Guid WallId), StoreDocs> Docs { get; } = [];

    /// <summary>Runs inside the next DocsAsync, before it answers (a label that changes mid-read).</summary>
    public Func<Task>? DuringDocs { get; set; }

    public int DocsReads { get; private set; }

    public int Clears { get; private set; }

    public Func<Task>? Listener { get; private set; }

    /// <summary>Held by the next SnapshotAsync after it read <see cref="Snapshot"/>, until completed (a slow read).</summary>
    public TaskCompletionSource? SnapshotGate { get; set; }

    public int SnapshotReads { get; private set; }

    public async Task<StoreSnapshot> SnapshotAsync()
    {
        SnapshotReads++;
        var taken = Snapshot;
        if (SnapshotGate is { } gate)
        {
            SnapshotGate = null;
            await gate.Task;
        }

        return taken;
    }

    public async Task<StoreDocs?> DocsAsync(string partition, Guid wallId)
    {
        DocsReads++;
        if (DuringDocs is { } during)
        {
            DuringDocs = null;
            await during();
        }

        return Docs.GetValueOrDefault((partition, wallId));
    }

    /// <summary>Runs inside the next ClearAsync, before it clears (this tab's own channel post, say).</summary>
    public Action? DuringClear { get; set; }

    public Task<bool> ClearAsync()
    {
        Clears++;
        if (DuringClear is { } during)
        {
            DuringClear = null;
            during();
        }

        Snapshot = StoreSnapshot.Nothing("empty");
        return Task.FromResult(true);
    }

    public Task ListenAsync(Func<Task> changed)
    {
        Listener = changed;
        return Task.CompletedTask;
    }
}
