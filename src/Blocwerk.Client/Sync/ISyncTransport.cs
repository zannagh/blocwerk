// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// Where the sync loop talks to the server and the device cache: client-sync.js in the browser
/// (<see cref="JsSyncTransport"/>), a fake stream and server in the tests. Every call answers; a failure is a status of
/// 0 or null, never an exception.
/// </summary>
public interface ISyncTransport
{
    /// <summary>Refreshes the wall's docs index into the ledger and answers what it now names.</summary>
    /// <param name="partition">The label the store reads.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="token">A share link's token, or null.</param>
    /// <returns>The index.</returns>
    Task<SyncIndex> RefreshIndexAsync(string partition, Guid wallId, string? token);

    /// <summary>One data document's JSON (<c>/d/{kind}/{sha}</c>; the service worker caches it under the partition).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="sha">Its sha.</param>
    /// <param name="token">A share link's token, or null.</param>
    /// <returns>The JSON, or null.</returns>
    Task<string?> FetchDocumentAsync(Guid wallId, string kind, string sha, string? token);

    /// <summary>Refreshes the viewer's marks of the wall into the ledger (Web's asset-mine.js); a 200 carries the body.</summary>
    /// <param name="partition">The <c>u-</c> label.</param>
    /// <param name="wallId">The wall.</param>
    /// <returns>The answer.</returns>
    Task<SyncResponse> RefreshMineAsync(string partition, Guid wallId);

    /// <summary>One catch-up page, <c>GET /api/walls/{w}/changes?after=</c>.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="after">The cursor, or null.</param>
    /// <param name="token">A share link's token, or null.</param>
    /// <returns>The answer.</returns>
    Task<SyncResponse> ChangesAsync(Guid wallId, string? after, string? token);

    /// <summary>
    /// One page of the viewer's own rows across walls, <c>GET /api/me/changes?after=</c>; a 200 carries the label the
    /// cookie names after it (<see cref="SyncResponse.Partition"/>).
    /// </summary>
    /// <param name="after">The cursor, or null.</param>
    /// <returns>The answer.</returns>
    Task<SyncResponse> MyChangesAsync(string? after);

    /// <summary>The wall's stored resume cursor (the ledger row's <c>feedCursor</c>), or null.</summary>
    /// <param name="partition">The label.</param>
    /// <param name="wallId">The wall.</param>
    /// <returns>The cursor, or null.</returns>
    Task<string?> SavedCursorAsync(string partition, Guid wallId);

    /// <summary>Stores the wall's resume cursor: everything at or before it is in the device's documents and marks.</summary>
    /// <param name="partition">The label.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="cursor">The cursor.</param>
    /// <returns>A task that completes once written (or refused: another label, no row).</returns>
    Task SaveCursorAsync(string partition, Guid wallId, string cursor);

    /// <summary>The own-rows poll's stored cursor for <paramref name="partition"/>, or null.</summary>
    /// <param name="partition">The <c>u-</c> label.</param>
    /// <returns>The cursor, or null.</returns>
    Task<string?> SavedMeCursorAsync(string partition);

    /// <summary>Stores the own-rows poll's cursor for <paramref name="partition"/>.</summary>
    /// <param name="partition">The <c>u-</c> label.</param>
    /// <param name="cursor">The cursor.</param>
    /// <returns>A task that completes once written.</returns>
    Task SaveMeCursorAsync(string partition, string cursor);

    /// <summary>Opens the wall's stream after <paramref name="after"/>; disposing it closes the stream.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="after">The cursor, or null (the server opens with a resync).</param>
    /// <param name="token">A share link's token, or null.</param>
    /// <param name="sink">Hears the stream.</param>
    /// <returns>The open stream.</returns>
    IDisposable OpenStream(Guid wallId, string? after, string? token, ISyncStreamSink sink);

    /// <summary>
    /// A refusal, by the plan's identity matrix (§3; client-store.js <c>refused</c>): 403/404 drop the wall (with
    /// <paramref name="mine"/>, only its marks), a 401 <c>expired</c> locks a <c>u-</c> label, any other 401 and any 410
    /// wipe; Server tabs hear the lock or wipe over asset-clear.js's channel.
    /// </summary>
    /// <param name="partition">The label the request was made under.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="status">The status.</param>
    /// <param name="reason">The sign-out reason, or null.</param>
    /// <param name="mine">True for a refusal of the marks alone.</param>
    /// <returns>client-store.js's outcome: dropped, locked, wiped, kept or switched.</returns>
    Task<string> RefusedAsync(string partition, Guid wallId, int status, string? reason, bool mine);

    /// <summary>Listens for the page's signals.</summary>
    /// <param name="listener">Hears them.</param>
    /// <returns>True when the tab is visible now.</returns>
    Task<bool> WatchAsync(ISyncSignals listener);

    /// <summary>A connection-state bubble (<c>window.bwStatus?.show</c>): <c>offline</c> or <c>online</c>.</summary>
    /// <param name="kind">The bubble.</param>
    void ShowStatus(string kind);
}
