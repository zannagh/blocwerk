// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Mine;

namespace Blocwerk.Client.Store;

/// <summary>The signed-in viewer's own marks on one cached wall (the ledger's <c>mine</c> store, <c>u-</c> only).</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Cursor">The feed cursor the marks were read at, or null.</param>
/// <param name="Ticks">The viewer's log per boulder.</param>
/// <param name="Favourites">The boulders the viewer marked as favourites.</param>
public sealed record StoreMineRow(Guid WallId, string? Cursor, IReadOnlyList<MineDocumentTick> Ticks, IReadOnlyList<Guid> Favourites);
