namespace Blocwerk.Web.State;

/// <summary>
/// One in-flight editing session tracked by <see cref="EditActivityRegistry"/>. Keyed in the
/// registry by a per-lease <see cref="Guid"/>, so the same user opening two editors counts twice
/// and each is removed independently when its own lease is disposed.
/// </summary>
/// <param name="StartedUtc">When the session was registered. Never moves.</param>
/// <param name="LastSeenUtc">
/// The last time the owning circuit proved it was still CONNECTED (see
/// <see cref="EditActivityCircuitHandler"/>). <see cref="EditActivityPolicy.LeaseTimeToLive"/> is
/// measured from this: an entry whose circuit has gone silent stops being counted instead of holding
/// the deploy gate until the process restarts.
/// </param>
/// <param name="LastActivityUtc">
/// The last time a HUMAN did something in the editor: real pointer, key or wheel input reported by
/// <c>edit-presence.js</c> (see <see cref="CircuitEditActivity.RecordActivity"/>). Never a heartbeat,
/// a reconnect, a render acknowledgement or server work. <see cref="EditActivityPolicy.InactivityTimeToLive"/>
/// and the deploy gate's presence window (<see cref="EditPresenceOptions.PresenceWindow"/>) are measured from this. Kept
/// apart from <paramref name="LastSeenUtc"/> on purpose: a tab abandoned on a gym tablet keeps its
/// socket alive indefinitely, and a signal that cannot tell that apart from someone working is the
/// signal that let the original 95-minute leak look healthy.
/// </param>
/// <param name="HiddenSinceUtc">
/// When the editor's page was hidden, frozen or unloaded (and not shown again since), or <c>null</c> while
/// it is visible. Diagnostics only: a hidden editor still holds the deploy gate (see <see cref="EditPresenceOptions"/>).
/// </param>
/// <param name="PresenceReported">
/// Whether the browser has reported presence at all (input or visibility) for this lease. A lease that never does is
/// one whose page has no working presence reporter, and it would look idle to the deploy gate however busy its user
/// is; <see cref="EditActivityCircuitHandler"/> logs a warning for it.
/// </param>
public sealed record EditActivityEntry(
    EditKind EditKind,
    Guid? WallId,
    Guid? UserId,
    DateTimeOffset StartedUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset LastActivityUtc,
    DateTimeOffset? HiddenSinceUtc = null,
    bool PresenceReported = false);
