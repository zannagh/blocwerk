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

    public Task<StoreSnapshot> SnapshotAsync() => Task.FromResult(Snapshot);

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

    public Task<bool> ClearAsync()
    {
        Clears++;
        Snapshot = StoreSnapshot.Nothing("empty");
        return Task.FromResult(true);
    }

    public Task ListenAsync(Func<Task> changed)
    {
        Listener = changed;
        return Task.CompletedTask;
    }
}
