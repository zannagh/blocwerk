// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Contracts.Documents;

/// <summary>One comment of a <see cref="CommentsDocument"/>, shaped like a hydrated <c>comment.added</c> envelope.</summary>
/// <param name="Id">The comment (the <c>entityId</c> of its <c>comment.added</c> and <c>comment.deleted</c>).</param>
/// <param name="BoulderId">The boulder it is on.</param>
/// <param name="Author">
/// The author as the class sees them, by the feed's rule: an erased, deleted or Ghost author is the placeholder; the id
/// only for the wall's members, or for an author with an avatar.
/// </param>
/// <param name="Text">The full text.</param>
/// <param name="CreatedAt">When it was posted.</param>
public sealed record CommentsDocumentComment(Guid Id, Guid BoulderId, FeedActor Author, string Text, DateTimeOffset CreatedAt);
