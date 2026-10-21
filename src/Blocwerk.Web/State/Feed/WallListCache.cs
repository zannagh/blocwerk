// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// The circuit's wall list (<c>GetMyWallsAsync</c>), memoised, and when a commit makes it stale. The list spans walls, so
/// no wall subscription can keep it fresh; it follows the in-process commits (<see cref="FeedCommits"/>) instead:
/// <list type="bullet">
/// <item><description>a row about someone's set of walls: <c>walls.changed</c> (joined, or created a wall),
/// <c>access.revoked</c> (removed or demoted) and <c>user.resync</c> (an account merged in). Whose it is does not matter
/// here: a circuit does not know whose list it holds without a query, and these are rare;</description></item>
/// <item><description>a change of a listed wall that the list shows: <c>wall.updated</c> (name, owner, settings),
/// <c>wall.resync</c> (a deleted wall among them) and <c>member.changed</c> (its members and roles). While the list is
/// still loading, any wall counts.</description></item>
/// </list>
/// </summary>
public sealed class WallListCache
{
    private static readonly IReadOnlySet<string> AboutWalls = new HashSet<string>(StringComparer.Ordinal)
    {
        FeedKinds.WallsChanged, FeedKinds.AccessRevoked, FeedKinds.UserResync,
    };

    private static readonly IReadOnlySet<string> Shown = new HashSet<string>(StringComparer.Ordinal)
    {
        FeedKinds.WallUpdated, FeedKinds.WallResync, FeedKinds.MemberChanged,
    };

    private readonly object gate = new();
    private Task<List<Wall>>? walls;

    /// <summary>True when the commit may let a signed-in user see a wall they could not before (cached "no access" answers go).</summary>
    public static bool GrantsWalls(FeedCommit commit) =>
        commit.Rows.Any(r => r.Kind is FeedKinds.WallsChanged or FeedKinds.UserResync);

    /// <summary>The memoised list; a failed load is dropped, so the next call retries.</summary>
    public Task<List<Wall>> GetAsync(Func<Task<List<Wall>>> load)
    {
        lock (gate)
        {
            if (walls is null)
            {
                var loading = load();
                walls = loading;
                _ = loading.ContinueWith(
                    ForgetFailed, CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
            }

            return walls;
        }
    }

    /// <summary>Drops the list; true when one was cached or loading.</summary>
    public bool Forget()
    {
        lock (gate)
        {
            var had = walls is not null;
            walls = null;
            return had;
        }
    }

    /// <summary>True when a list is cached or loading and the commit may have changed it.</summary>
    public bool IsStaleAfter(FeedCommit commit)
    {
        Task<List<Wall>>? current;
        lock (gate)
        {
            current = walls;
        }

        if (current is null)
        {
            return false;
        }

        var listed = current.IsCompletedSuccessfully ? current.Result.Select(w => w.Id).ToHashSet() : null;
        return commit.Rows.Any(r =>
            (r.Audience == FeedAudience.User && AboutWalls.Contains(r.Kind))
            || (r.WallId is { } id && Shown.Contains(r.Kind) && (listed?.Contains(id) ?? true)));
    }

    // Only that load: a newer one started after a Forget stays.
    private void ForgetFailed(Task<List<Wall>> failed)
    {
        lock (gate)
        {
            if (ReferenceEquals(walls, failed))
            {
                walls = null;
            }
        }
    }
}
