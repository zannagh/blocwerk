// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// One committed row as an observer of <see cref="FeedCommits"/> sees it: the actor-free envelope (ids, kind, payload) and
/// the row's audience class. Who acted and whom a User row is addressed to are not handed out.
/// </summary>
/// <param name="Envelope">The envelope, carrying <see cref="FeedCommit.Cursor"/>, no actor and no content.</param>
/// <param name="Audience">The row's audience, e.g. <see cref="FeedAudience.User"/> for a row addressed to one user.</param>
public sealed record FeedCommitEvent(FeedEnvelope Envelope, FeedAudience Audience);
