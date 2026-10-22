// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// A wall's asset manifest (design §6.4), served by <c>GET /api/walls/{wallId}/manifest</c>: every hashed asset the
/// requesting audience may fetch through <c>/api/walls/{wallId}/a/{sha}</c>, with its tier, so a client can fill and
/// verify its cache. It holds nothing per user and names no partition.
/// </summary>
/// <param name="V">The manifest schema version (<see cref="ManifestSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Sha">
/// The "wall hash": SHA-256 (lowercase hex) of the canonical JSON of this manifest without <see cref="Sha"/> and
/// <see cref="Cursor"/>. It is the response's weak ETag, so an unchanged manifest answers <c>If-None-Match</c> with a 304.
/// </param>
/// <param name="Cursor">
/// The feed cursor the manifest was read at; every change at or below it is reflected. Members only (their client
/// catches up from the feed by cursor); left out for share-link and kiosk viewers.
/// </param>
/// <param name="Generation">The wall's current generation.</param>
/// <param name="Pending">How many listed assets have no hash yet (<c>sha: null</c>): network-only until hashed.</param>
/// <param name="Totals">
/// The listed bytes per tier: the per-wall figures a client orders its fill and picks evictions by. No wall is left out
/// of the cache for its size; wall data is always cached (decided 2026-10-21).
/// </param>
/// <param name="Assets">The assets, ordered by tier, kind and id; one entry per distinct sha.</param>
public sealed record WallManifest(
    int V,
    Guid WallId,
    string? Sha,
    string? Cursor,
    int Generation,
    int Pending,
    ManifestTotals Totals,
    IReadOnlyList<ManifestAsset> Assets);
