// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Store;

/// <summary>A cached wall of the active partition, as the ledger records it.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Data">Its data documents by kind (<c>Blocwerk.Contracts.Documents.DataDocumentKinds</c>).</param>
public sealed record StoreWallRow(Guid WallId, IReadOnlyDictionary<string, StoreDocRef> Data);
