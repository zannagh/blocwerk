// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>
/// The <c>comments</c> document: every comment of every boulder the class may read, with its full text, as the boulder
/// page shows them. A deleted comment, and every comment of a deleted boulder, is simply absent: the next sha replaces the
/// old one. Nothing in it is about the reader.
/// </summary>
/// <param name="V">The schema version (<see cref="DataDocumentSchema.Version"/>).</param>
/// <param name="WallId">The wall.</param>
/// <param name="Comments">The comments, by boulder, then oldest first, then by id.</param>
public sealed record CommentsDocument(int V, Guid WallId, IReadOnlyList<CommentsDocumentComment> Comments);
