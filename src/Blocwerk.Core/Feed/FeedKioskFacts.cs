// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// What one read's rows need for the kiosk tablet, read at delivery so it is current: which <c>activity.added</c> entries
/// it may read (<see cref="KioskActivityVisibility"/>), which of those name their setter whatever their consent
/// (<see cref="KioskBylines.KeepsName"/>), and which row authors consented to the kiosk on the row's wall.
/// </summary>
public sealed class FeedKioskFacts
{
    public static readonly FeedKioskFacts None = new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<(Guid, Guid)>(), new HashSet<Guid>());

    private FeedKioskFacts(
        IReadOnlySet<Guid> readable,
        IReadOnlySet<Guid> setterEntries,
        IReadOnlySet<(Guid WallId, Guid UserId)> consented,
        IReadOnlySet<Guid> openBoulders)
    {
        Readable = readable;
        SetterEntries = setterEntries;
        Consented = consented;
        OpenBoulders = openBoulders;
    }

    /// <summary>Gets the activity entries (by entity id) a kiosk may read now; an id missing here is not for the kiosk.</summary>
    public IReadOnlySet<Guid> Readable { get; }

    /// <summary>Gets the readable activity entries whose author the resting kiosk names whatever their consent.</summary>
    public IReadOnlySet<Guid> SetterEntries { get; }

    /// <summary>Gets the (wall, user) pairs of row authors who consented to the kiosk on that wall.</summary>
    public IReadOnlySet<(Guid WallId, Guid UserId)> Consented { get; }

    /// <summary>
    /// Gets the boulders of the read's <c>activity.removed</c> rows that are published and not archived now: the entry is
    /// gone, so its boulder decides whether the tablet may hear of it (it never learns a draft's id).
    /// </summary>
    public IReadOnlySet<Guid> OpenBoulders { get; }

    /// <summary>True when the resting kiosk may name <paramref name="row"/>'s author (<see cref="KioskBylines"/>).</summary>
    public bool Names(WallChange row) =>
        (row.ActorUserId is { } actor && row.WallId is { } wall && Consented.Contains((wall, actor)))
        || (row.Kind == FeedKinds.ActivityAdded && row.EntityId is { } entry && SetterEntries.Contains(entry));

    /// <summary>Three queries at most, one per kind of fact, and none for a read that needs none.</summary>
    public static async Task<FeedKioskFacts> LoadAsync(BlocwerkDbContext db, IReadOnlyList<WallChange> rows, CancellationToken cancellationToken)
    {
        var entryIds = rows.Where(r => r.Kind == FeedKinds.ActivityAdded && r.EntityId is not null)
            .Select(r => r.EntityId!.Value).Distinct().ToList();
        var entries = entryIds.Count == 0 ? [] : await db.ActivityLog.AsNoTracking()
            .Where(a => entryIds.Contains(a.Id))
            .Where(KioskActivityVisibility.Readable)
            .Select(a => new { a.Id, a.Type })
            .ToListAsync(cancellationToken);

        var wallIds = rows.Where(r => r.WallId is not null).Select(r => r.WallId!.Value).Distinct().ToList();
        var actorIds = rows.Where(r => r.ActorUserId is not null).Select(r => r.ActorUserId!.Value).Distinct().ToList();
        var consented = wallIds.Count == 0 || actorIds.Count == 0 ? [] : await db.WallMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => wallIds.Contains(m.WallId) && actorIds.Contains(m.UserId) && m.KioskConsentedAt != null)
            .Select(m => new { m.WallId, m.UserId })
            .ToListAsync(cancellationToken);

        return new FeedKioskFacts(
            entries.Select(e => e.Id).ToHashSet(),
            entries.Where(e => KioskBylines.KeepsName(e.Type)).Select(e => e.Id).ToHashSet(),
            consented.Select(m => (m.WallId, m.UserId)).ToHashSet(),
            await OpenBouldersAsync(db, rows, cancellationToken));
    }

    private static async Task<IReadOnlySet<Guid>> OpenBouldersAsync(
        BlocwerkDbContext db, IReadOnlyList<WallChange> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.Kind == FeedKinds.ActivityRemoved && r.BoulderId is not null)
            .Select(r => r.BoulderId!.Value).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var open = await db.Boulders.IgnoreQueryFilters().AsNoTracking()
            .Where(b => ids.Contains(b.Id) && !b.IsDraft && !b.IsArchived)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);
        return open.ToHashSet();
    }
}
