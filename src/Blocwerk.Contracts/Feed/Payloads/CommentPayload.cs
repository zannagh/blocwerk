// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>comment.added</c> and <c>comment.deleted</c>; the text is hydrated at read time.</summary>
public sealed record CommentPayload(Guid BoulderId) : FeedPayload;
