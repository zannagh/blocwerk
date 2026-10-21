// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// A <see cref="FeedScope"/> for a journal replay or revert, which applies a whole batch in one transaction. Only a
/// wall-scoped batch gets one: a boulder's batch is small, and an unscoped one names no wall to resync.
/// </summary>
internal static class JournalFeedScope
{
    /// <summary>Opens the scope (dispose it with the journal batch), or returns null when the batch is not a wall's.</summary>
    public static IDisposable? Begin(ChangeJournalScopeKind kind, Guid? scopeId, string label) =>
        kind == ChangeJournalScopeKind.Wall && scopeId is { } wallId ? FeedScope.Begin(wallId, label) : null;
}
