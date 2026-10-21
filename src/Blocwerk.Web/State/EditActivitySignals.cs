// <copyright file="EditActivitySignals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// How each <see cref="EditSignal"/> changes an <see cref="EditActivityEntry"/>, in one place so the registry and the
/// circuit's remembered template can never disagree about what a signal means.
/// </summary>
/// <remarks>
/// The rule that matters: only <see cref="EditSignal.Input"/> moves <see cref="EditActivityEntry.LastActivityUtc"/>.
/// A heartbeat, a reconnect, a visibility change, a render acknowledgement or server-side work after an edit are all
/// things a sleeping laptop's tab can produce by itself (macOS dark wake reconnects the socket every few minutes).
/// </remarks>
public static class EditActivitySignals
{
    /// <summary>The change <paramref name="signal"/> makes to an entry at <paramref name="now"/>.</summary>
    public static Func<EditActivityEntry, EditActivityEntry> For(EditSignal signal, DateTimeOffset now)
    {
        return signal switch
        {
            EditSignal.Input => e => e with { LastSeenUtc = now, LastActivityUtc = now, HiddenSinceUtc = null, PresenceReported = true },
            EditSignal.Hidden => e => e with { HiddenSinceUtc = e.HiddenSinceUtc ?? now, PresenceReported = true },
            EditSignal.Visible => e => e with { HiddenSinceUtc = null, PresenceReported = true },
            _ => e => e with { LastSeenUtc = now },
        };
    }
}
