// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// What one feed delivery means for one live leaf list (design §5.5): the events of its scope, in feed order, or a reload.
/// A reload replaces patching: the delivery was a resync, a payload was of an unknown schema, the wall was coarsely
/// resynced, or the circuit's viewer on the wall changed (rows cut at a revoke were dropped, design PR 1.4 notes).
/// </summary>
/// <param name="Events">The events of the list's scope the list has not loaded yet.</param>
/// <param name="Reload">True when the list must re-read instead of patching.</param>
/// <param name="Structural">
/// True when the reload stems from the wall's content (a <c>wall.resync</c>, a coarse kind of the scope, a payload of an
/// unknown schema), not only from a delivery resync or a changed viewer: what the list shows may really have changed.
/// </param>
public sealed record LeafBatch(IReadOnlyList<FeedEnvelope> Events, bool Reload, bool Structural = false)
{
    /// <summary>Gets a batch that only asks for a reload (a delivery resync, a changed viewer).</summary>
    public static LeafBatch ReloadOnly { get; } = new([], true);

    /// <summary>Gets a batch that asks for a reload because the wall's content changed.</summary>
    public static LeafBatch Coarse { get; } = new([], true, true);

    /// <summary>
    /// One batch out of two, the later one's events after this one's: used while a list is held (the user is editing in
    /// it), so nothing that arrives meanwhile is lost.
    /// </summary>
    public LeafBatch Merge(LeafBatch later) =>
        new([.. Events, .. later.Events], Reload || later.Reload, Structural || later.Structural);

    /// <summary>
    /// What is left of this batch after a load at <paramref name="at"/>: the events above it (an event of another epoch, or
    /// with a cursor that does not parse, stays), and the reload. Null when nothing is left.
    /// </summary>
    public LeafBatch? After(FeedCursor? at)
    {
        if (at is not { } cursor)
        {
            return this;
        }

        var left = Events.Where(e => !FeedCursor.TryParse(e.Cursor, out var c) || c.Epoch != cursor.Epoch || c.Position > cursor.Position).ToList();
        return left.Count == 0 && !Reload ? null : new LeafBatch(left, Reload, Structural);
    }
}
