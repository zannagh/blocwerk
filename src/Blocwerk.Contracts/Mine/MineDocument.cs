// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;

namespace Blocwerk.Contracts.Mine;

/// <summary>
/// <c>GET /api/me/walls/{w}/mine</c>: the signed-in reader's own marks on one wall (Phase 3 plan §1, PR 3.4), for the
/// offline client's boulder list and detail. Unlike the data documents it is one reader's: it is never stored by a cache,
/// has no sha, and lives outside <c>Blocwerk.Contracts.Documents</c> (whose contracts carry nothing per reader, a scan
/// pins that). The device keeps it in the ledger's <c>mine</c> store of its <c>u-</c> partition only.
/// </summary>
/// <param name="V">The schema version (<see cref="DataDocumentSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Cursor">The feed mark read before the marks were: the caller's own rows after it are replayed.</param>
/// <param name="Ticks">One entry per boulder the reader logged an attempt on, by boulder id.</param>
/// <param name="Favourites">The reader's favourite boulders of the wall, by id.</param>
public sealed record MineDocument(
    int V, Guid WallId, string Cursor, IReadOnlyList<MineDocumentTick> Ticks, IReadOnlyList<Guid> Favourites);
