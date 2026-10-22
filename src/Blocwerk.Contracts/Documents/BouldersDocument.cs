// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>
/// The <c>boulders</c> document: the wall's active boulders (not archived, not historic) with their holds, byline and
/// wall-wide stats. Drafts are in the setters class only; nothing in it is about the reader.
/// </summary>
/// <param name="V">The schema version (<see cref="DataDocumentSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Boulders">The boulders, by id.</param>
public sealed record BouldersDocument(int V, Guid WallId, IReadOnlyList<BouldersDocumentBoulder> Boulders);
