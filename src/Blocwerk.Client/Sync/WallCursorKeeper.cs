// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// The wall's resume cursor on the device (the ledger row's <c>feedCursor</c>): the newest cursor whose events are all in
/// the device's documents and marks, so the next start (a reload, the tab shown again) replays from there, not from the
/// oldest document's build cursor. It moves only when the device caught up (an index or marks refetch landed, or an
/// event that needs neither), which is a few times per burst of edits, so every move is written.
/// </summary>
internal sealed class WallCursorKeeper
{
    private readonly ISyncTransport transport;
    private readonly string partition;
    private readonly Guid wallId;

    public WallCursorKeeper(ISyncTransport transport, string partition, Guid wallId)
    {
        this.transport = transport;
        this.partition = partition;
        this.wallId = wallId;
    }

    /// <summary>Gets the newest cursor the device's data is known to reflect.</summary>
    public string? Persisted { get; private set; }

    /// <summary>The stored cursor of an earlier run.</summary>
    /// <returns>The cursor, or null.</returns>
    public Task<string?> LoadAsync() => transport.SavedCursorAsync(partition, wallId);

    /// <summary>The device's data now reflects every event up to <paramref name="cursor"/>: store it.</summary>
    /// <param name="cursor">The cursor, or null (nothing known).</param>
    public void Advance(string? cursor)
    {
        if (cursor is null || (Persisted is not null && !SyncCursor.IsAfter(cursor, Persisted)))
        {
            return;
        }

        Persisted = cursor;
        _ = transport.SaveCursorAsync(partition, wallId, cursor);
    }
}
