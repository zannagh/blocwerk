// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;

namespace Blocwerk.Client.Sync;

/// <summary>
/// A wall's docs index after client-sync.js refreshed it (Web's asset-data.js: <c>GET /api/walls/{w}/docs</c> with the
/// stored ETag, the ledger row written, a replaced comments document deleted at once).
/// </summary>
/// <param name="Status">The index's status; 0 without one.</param>
/// <param name="Outcome">
/// asset-data.js's outcome: <c>updated</c> and <c>unchanged</c> carry <see cref="Docs"/>; <c>skipped</c> (the ledger row
/// is gone) and <c>switched</c> (another label is active now) mean the wall is no longer this device's to sync.
/// </param>
/// <param name="Reason">On a 401, the sign-out reason.</param>
/// <param name="Docs">The row's documents by kind as the ledger now names them, or null.</param>
public sealed record SyncIndex(int Status, string Outcome, string? Reason = null, IReadOnlyDictionary<string, StoreDocRef>? Docs = null)
{
    /// <summary>Gets a value indicating whether the wall's ledger row went or the device moved to another label.</summary>
    public bool Gone => Outcome is "skipped" or "switched";
}
