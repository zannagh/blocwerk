// <copyright file="CircuitEditLease.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// One lease held by a <see cref="CircuitEditActivity"/>. Wraps a registry lease so disposing it also drops the
/// circuit's tracking reference (the backstop list would otherwise grow across many enter/leave-edit cycles in one
/// long-lived circuit), and keeps the entry's template so the session can be re-registered under a NEW lease id if
/// the registry expired the old one while the client was away.
/// </summary>
internal sealed class CircuitEditLease(CircuitEditActivity owner, IEditLease lease, EditActivityEntry template)
    : IDisposable
{
    private readonly object gate = new();
    private CircuitEditActivity? owner = owner;
    private IEditLease? lease = lease;
    private EditActivityEntry template = template;

    /// <summary>
    /// Applies <paramref name="signal"/> to the live entry, or (when <paramref name="restore"/> and the registry
    /// has dropped it) re-registers it from the template. Returns whether the session is now in the registry.
    /// </summary>
    public bool Pulse(EditActivityRegistry registry, DateTimeOffset now, EditSignal signal, bool restore)
    {
        IEditLease? current;
        EditActivityEntry snapshot;
        lock (gate)
        {
            if (owner is null)
            {
                return false;
            }

            current = lease;
            snapshot = template;
        }

        var change = EditActivitySignals.For(signal, now);
        if (current is not null && registry.Apply(current.Id, change, now))
        {
            Remember(change(snapshot));
            return true;
        }

        return restore && Restore(registry, now, change(snapshot), current);
    }

    public void Dispose()
    {
        CircuitEditActivity? current;
        IEditLease? held;
        lock (gate)
        {
            current = owner;
            held = lease;
            owner = null;
            lease = null;
        }

        if (current is null)
        {
            return;
        }

        current.Forget(this);
        held?.Dispose();
    }

    private bool Restore(EditActivityRegistry registry, DateTimeOffset now, EditActivityEntry candidate, IEditLease? stale)
    {
        // The registry has dropped the entry. Hand it back the history it had, so the policy — not this class —
        // decides whether the session is genuinely over.
        var replacement = registry.Restore(candidate, now);
        if (replacement is null)
        {
            return false;
        }

        stale?.Dispose();

        lock (gate)
        {
            if (owner is null)
            {
                replacement.Dispose();
                return false;
            }

            lease = replacement;
            template = candidate with { LastSeenUtc = now };
        }

        return true;
    }

    private void Remember(EditActivityEntry updated)
    {
        lock (gate)
        {
            if (owner is not null)
            {
                template = updated;
            }
        }
    }
}
