using System.Runtime.CompilerServices;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Data;

/// <summary>
/// Publishes <see cref="DomainChange"/> notifications for wall/boulder mutations automatically,
/// so callers never have to remember to fire them. This is the single seam that keeps the
/// per-circuit caches coherent: every service mutates through a <see cref="BlocwerkDbContext"/>,
/// so intercepting <c>SaveChanges</c> catches them all — current and future — in one place.
///
/// Affected ids are collected in the <c>SavingChanges</c> pass (entity states are still available
/// there) and only published in the <c>SavedChanges</c> pass, i.e. after the write actually
/// committed. State is stashed per-<see cref="DbContext"/> in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// so concurrent saves on different contexts can't clobber each other (the interceptor is a singleton).
/// </summary>
public sealed class DomainChangeInterceptor : SaveChangesInterceptor
{
    private readonly IDomainChangeNotifier notifier;
    private readonly ConditionalWeakTable<DbContext, List<DomainChange>> pending = new();

    public DomainChangeInterceptor(IDomainChangeNotifier notifier)
    {
        this.notifier = notifier;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var changes = new HashSet<DomainChange>();
        var wallListTouched = false;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            switch (entry.Entity)
            {
                case Wall wall:
                    changes.Add(new DomainChange(DomainChangeScope.Wall, wall.Id, Guid.Empty));
                    wallListTouched = true;
                    break;
                case Boulder boulder:
                    changes.Add(new DomainChange(DomainChangeScope.Boulder, boulder.WallId, boulder.Id));
                    break;
                case Hold hold:
                    changes.Add(new DomainChange(DomainChangeScope.Wall, hold.WallId, Guid.Empty));
                    break;
                case WallSegment segment:
                    changes.Add(new DomainChange(DomainChangeScope.Wall, segment.WallId, Guid.Empty));
                    break;
                case WallPanel panel:
                    changes.Add(new DomainChange(DomainChangeScope.Wall, panel.WallId, Guid.Empty));
                    break;
                case HoldLink holdLink:
                    changes.Add(new DomainChange(DomainChangeScope.Wall, holdLink.WallId, Guid.Empty));
                    break;
                case WallMember member:
                    changes.Add(new DomainChange(DomainChangeScope.Wall, member.WallId, Guid.Empty));
                    wallListTouched = true;
                    break;
                case BoulderHold boulderHold:
                    // The parent wall is only known when the Boulder navigation is loaded (it is
                    // during a create/revise). When it isn't, WallId stays Empty — the cache still
                    // evicts the boulder itself and derives the wall from its own cached copy.
                    var wallId = boulderHold.Boulder?.WallId ?? Guid.Empty;
                    changes.Add(new DomainChange(DomainChangeScope.Boulder, wallId, boulderHold.BoulderId));
                    break;
            }
        }

        if (wallListTouched)
        {
            changes.Add(new DomainChange(DomainChangeScope.WallList, Guid.Empty, Guid.Empty));
        }

        if (changes.Count > 0)
        {
            pending.AddOrUpdate(context, changes.ToList());
        }
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !pending.TryGetValue(context, out var changes))
        {
            return;
        }

        pending.Remove(context);
        foreach (var change in changes)
        {
            notifier.Publish(change);
        }
    }
}
