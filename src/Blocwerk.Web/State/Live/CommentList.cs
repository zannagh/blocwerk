// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// A boulder's comment list, newest first, kept live from the feed: a hydrated <c>comment.added</c> is inserted (or
/// replaces its row), a <c>comment.deleted</c> or a tombstone drops it. The list is a window over the newest
/// <see cref="Window"/> comments; "Load more" widens it and re-reads, so an insert or delete in between can never shift a
/// page boundary into a duplicate or a gap.
/// </summary>
public sealed class CommentList
{
    public const int PageSize = 10;

    private static readonly HashSet<string> UpsertKinds = [FeedKinds.CommentAdded];
    private static readonly HashSet<string> RemoveKinds = [FeedKinds.CommentDeleted];
    private readonly List<CommentRow> rows = [];

    /// <summary>Gets the kinds the comment list follows.</summary>
    public static IReadOnlySet<string> Kinds { get; } =
        new HashSet<string> { FeedKinds.CommentAdded, FeedKinds.CommentDeleted, FeedKinds.UserProfile };

    public IReadOnlyList<CommentRow> Rows => rows;

    public int Total { get; private set; }

    /// <summary>Gets how many of the newest comments a re-read loads.</summary>
    public int Window { get; private set; } = PageSize;

    public bool HasMore => Total > rows.Count;

    /// <summary>Replaces the rows with a fresh read of the window.</summary>
    public void Load(IEnumerable<BoulderComment> items, int total, FeedViewer? viewer)
    {
        rows.Clear();
        rows.AddRange(items.Select(c => CommentRow.Of(c, viewer)));
        Total = total;
    }

    /// <summary>Widens the window by a page; the caller re-reads.</summary>
    public void Grow() => Window = Math.Max(Window, rows.Count) + PageSize;

    /// <summary>Drops a comment the viewer deleted here.</summary>
    public void Drop(Guid id)
    {
        if (rows.RemoveAll(r => r.Id == id) > 0)
        {
            Total--;
        }
    }

    /// <summary>
    /// Patches the rows; true when the list must re-read instead: a reload, a changed byline it shows, or (with
    /// <paramref name="signedIn"/>) a new comment whose author id the feed withheld from this viewer (a kiosk acting as a
    /// member, a share viewer), since only the query can tell whether it is the viewer's own and may be deleted here.
    /// </summary>
    public bool Apply(LeafBatch batch, bool signedIn = false)
    {
        var plan = LeafPlan.Of(batch, UpsertKinds, RemoveKinds);
        if (plan.Reload || rows.Any(r => r.OwnerId is { } owner && plan.Profiles.Contains(owner))
            || (signedIn && plan.Upserts.Any(e => !e.Tombstone && e.Actor?.Id is null && rows.All(r => r.Id != e.EntityId))))
        {
            return true;
        }

        foreach (var id in plan.Removals)
        {
            Remove(id, plan.Deletions.Contains(id));
        }

        foreach (var e in plan.Upserts)
        {
            if (CommentRow.Of(e) is { } row)
            {
                Upsert(row);
            }
        }

        return false;
    }

    // A deleted comment beyond a partial window is not shown, but it still counted.
    private void Remove(Guid id, bool deleted)
    {
        if (rows.RemoveAll(r => r.Id == id) > 0 || (deleted && HasMore))
        {
            Total--;
        }
    }

    private void Upsert(CommentRow row)
    {
        var at = rows.FindIndex(r => r.Id == row.Id);
        if (at >= 0)
        {
            rows[at] = row with { OwnerId = rows[at].OwnerId ?? row.OwnerId };
            return;
        }

        // Newest first. A comment older than the last row of a partial window belongs to a page not loaded yet.
        var index = rows.FindIndex(r => r.CreatedAt < row.CreatedAt);
        var beyondWindow = index < 0 && HasMore;
        Total++;
        if (beyondWindow)
        {
            return;
        }

        rows.Insert(index < 0 ? rows.Count : index, row);
        Window = Math.Max(Window, rows.Count);
    }
}
