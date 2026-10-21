// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>Content read from the source tables when an event is delivered (a comment's text). Never stored in the feed.</summary>
/// <param name="Text">The text, when the kind has any.</param>
/// <param name="CreatedAt">When the source row was created.</param>
public sealed record FeedContent(string? Text, DateTimeOffset? CreatedAt);
