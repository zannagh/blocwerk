// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>Who made a change, as the viewer may see them: resolved at read time, never stored in the feed.</summary>
/// <param name="Id">The user id (the Ghost id for an erased or unknown person).</param>
/// <param name="Name">The display name, or the placeholder name.</param>
/// <param name="HasAvatar">Whether <c>/api/users/{id}/avatar</c> has an image.</param>
/// <param name="AvatarSha">The avatar's SHA-256 (hex) once avatars are hashed (PR 2.1); null until then.</param>
public sealed record FeedActor(Guid Id, string Name, bool HasAvatar, string? AvatarSha);
