// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>
/// Collapses a known bulk operation's feed rows (design §5.3, N1): inside a scope a save writes no fine-grained rows, only
/// one <c>wall.resync</c> per wall it touched, so a promote, a staging run or a journal replay costs a row per save
/// instead of thousands, and a save that also touches another wall still reaches that wall. Ambient (an
/// <see cref="AsyncLocal{T}"/>), so it flows into every context the operation's async flow saves through.
/// </summary>
public static class FeedScope
{
    private static readonly AsyncLocal<FeedScopeState?> Ambient = new();

    /// <summary>Gets the innermost scope that is still open, or null. A closed scope inherited by leftover work is skipped.</summary>
    public static FeedScopeState? Active
    {
        get
        {
            var scope = Ambient.Value;
            while (scope is { Closed: true })
            {
                scope = scope.Parent;
            }

            return scope;
        }
    }

    /// <summary>Opens a scope for an operation on <paramref name="wallId"/>. Dispose it when the operation ends.</summary>
    public static IDisposable Begin(Guid wallId, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var scope = new FeedScopeState(wallId, label, Active);
        Ambient.Value = scope;
        return new FeedScopeHandle(scope);
    }

    /// <summary>
    /// Opens a silent scope for an operation on a user across walls (account erasure, merge). Its saves write no wall
    /// rows, since a mapper or the boulder resolver cannot attribute them without a lookup per wall; rows for one user
    /// (<c>FeedAudience.User</c>) are kept. The operation appends one <c>wall.resync</c> per wall it touched itself, in
    /// its transaction (<see cref="FeedAccountRows"/>).
    /// </summary>
    public static IDisposable BeginAccount(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        var scope = new FeedScopeState(Guid.Empty, label, Active, silent: true);
        Ambient.Value = scope;
        return new FeedScopeHandle(scope);
    }

    internal static void End(FeedScopeState scope)
    {
        scope.Close();
        if (ReferenceEquals(Ambient.Value, scope))
        {
            Ambient.Value = scope.Parent;
        }
    }
}
