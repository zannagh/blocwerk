// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>A user row as hydration reads it, before <see cref="AuthorVisibility"/> decides what a viewer sees of it.</summary>
/// <param name="Id">The user id.</param>
/// <param name="Name">The effective display name (custom name, else the provider's).</param>
/// <param name="HasAvatar">Whether the user has an uploaded avatar.</param>
/// <param name="IsDeleted">Whether the row is an erased account's tombstone.</param>
public sealed record FeedAuthor(Guid Id, string Name, bool HasAvatar, bool IsDeleted);
