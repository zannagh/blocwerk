// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>The <c>gallery</c> document: the wall's gallery images, each fetched by sha through <c>/a/{sha}</c>.</summary>
/// <param name="V">The schema version (<see cref="DataDocumentSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Images">The images, newest capture first, then by id.</param>
public sealed record GalleryDocument(int V, Guid WallId, IReadOnlyList<GalleryDocumentImage> Images);
