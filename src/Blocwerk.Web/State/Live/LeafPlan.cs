// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// What a list does with one <see cref="LeafBatch"/>, keyed by entity id, last event per id winning: upsert the items whose
/// content the event carries (hydrated), remove the deleted and tombstoned ones, re-read when an event needs content the
/// envelope does not carry. A profile change is listed apart: only a list that shows that person re-reads.
/// </summary>
/// <param name="Upserts">Events to insert or replace by id, from their hydrated content.</param>
/// <param name="Removals">Ids to drop.</param>
/// <param name="Profiles">Users whose name or avatar changed.</param>
/// <param name="Reload">True when the list must re-read.</param>
public sealed record LeafPlan(
    IReadOnlyList<FeedEnvelope> Upserts, IReadOnlySet<Guid> Removals, IReadOnlySet<Guid> Profiles, bool Reload)
{
    /// <summary>
    /// The plan of <paramref name="batch"/>. A <paramref name="upsert"/> kind with hydrated content patches, and becomes a
    /// removal when it is a tombstone; a <paramref name="remove"/> kind removes; any other kind of the scope re-reads.
    /// </summary>
    public static LeafPlan Of(LeafBatch batch, IReadOnlySet<string> upsert, IReadOnlySet<string> remove)
    {
        var last = new Dictionary<Guid, FeedEnvelope>();
        var profiles = new HashSet<Guid>();
        var reload = batch.Reload;
        foreach (var e in batch.Events)
        {
            if (e.Kind == FeedKinds.UserProfile)
            {
                AddId(profiles, e.EntityId);
            }
            else if (e.EntityId is { } id && (remove.Contains(e.Kind) || (upsert.Contains(e.Kind) && (e.Tombstone || e.Hydrated is not null))))
            {
                last[id] = e;
            }
            else
            {
                reload = true;
            }
        }

        var removals = last.Where(p => p.Value.Tombstone || remove.Contains(p.Value.Kind)).Select(p => p.Key).ToHashSet();
        var upserts = last.Values.Where(e => !removals.Contains(e.EntityId!.Value)).ToList();
        return new LeafPlan(upserts, removals, profiles, reload);
    }

    private static void AddId(HashSet<Guid> ids, Guid? id)
    {
        if (id is { } value)
        {
            ids.Add(value);
        }
    }
}
