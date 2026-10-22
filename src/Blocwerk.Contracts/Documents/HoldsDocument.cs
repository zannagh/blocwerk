// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>The <c>holds</c> document: the wall's live holds (historic generations are not in it).</summary>
/// <param name="V">The schema version (<see cref="DataDocumentSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Generation">The wall's live generation.</param>
/// <param name="Holds">The holds, by id.</param>
public sealed record HoldsDocument(int V, Guid WallId, int Generation, IReadOnlyList<HoldsDocumentHold> Holds);
