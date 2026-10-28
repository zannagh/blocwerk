// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>
/// Where <see cref="ClientStore"/> reads the device cache: client-store.js in the browser (<see cref="JsClientStoreSource"/>),
/// a fake in the tests. Every call answers; a failure is <c>unavailable</c> or null, never an exception.
/// </summary>
public interface IClientStoreSource
{
    /// <summary>What the client may show now.</summary>
    /// <returns>The snapshot.</returns>
    Task<StoreSnapshot> SnapshotAsync();

    /// <summary>One wall's cached documents under <paramref name="partition"/>.</summary>
    /// <param name="partition">The label the snapshot named.</param>
    /// <param name="wallId">The wall.</param>
    /// <returns>The documents, or null when that label may no longer be read (another identity, a lock).</returns>
    Task<StoreDocs?> DocsAsync(string partition, Guid wallId);

    /// <summary>"Clear offline data": the active partition and a locked one go from the device.</summary>
    /// <returns>True when the clear ran.</returns>
    Task<bool> ClearAsync();

    /// <summary>Calls <paramref name="changed"/> whenever another tab or the identity may have changed what to show.</summary>
    /// <param name="changed">The callback.</param>
    /// <returns>A task that completes once listening.</returns>
    Task ListenAsync(Func<Task> changed);
}
