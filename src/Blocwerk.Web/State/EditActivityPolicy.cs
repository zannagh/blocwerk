namespace Blocwerk.Web.State;

/// <summary>
/// The decision half of the busy signal: how often a live circuit must prove it still holds its
/// editing leases, how long a lease survives without that proof, and how long a lease that is
/// connected but untouched by a human still counts as work in flight.
/// </summary>
/// <remarks>
/// <para>Split out from <see cref="EditActivityRegistry"/> so both halves can be exercised without a
/// circuit. Everything here is a pure function of a clock.</para>
/// <para><b>Why a TTL exists at all.</b> The registry is in memory and a lease is released by a
/// <c>Dispose</c> that .NET must get around to calling. On an abrupt client drop that only happens
/// after the disconnected-circuit retention period, and only once the hub has noticed the socket
/// died. A leaked lease therefore held <c>/health/ready-to-deploy</c> at <c>busy</c> until the
/// process restarted — while itself blocking the deploy that would have restarted it. The TTLs turn
/// that from an unbounded outage into a bounded one.</para>
/// <para><b>Why the numbers live here and not in <c>Program.cs</c>.</b> The circuit retention period
/// and the client timeout are inputs to the TTL, not independent knobs: a lease that dies while its
/// circuit is still recoverable is exactly the race this class exists to close. They are declared
/// here and read by <c>Program.cs</c> when it configures the hub, so the two cannot drift apart.</para>
/// </remarks>
public static class EditActivityPolicy
{
    /// <summary>
    /// How often a live circuit touches the leases it holds.
    /// </summary>
    /// <remarks>
    /// Matches the hub's <c>KeepAliveInterval</c> order of magnitude at 30s, and is the same order as
    /// <see cref="KioskCircuitPolicy.RevalidationInterval"/>. A touch is a dictionary write, so the
    /// cost of the interval is the timer itself, not the work.
    /// </remarks>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a client may be silent before the hub declares the connection down. Configured onto
    /// <c>HubOptions.ClientTimeoutInterval</c> in <c>Program.cs</c>.
    /// </summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a disconnected circuit's state is retained so the browser can reconnect in place.
    /// Configured onto <c>CircuitOptions.DisconnectedCircuitRetentionPeriod</c> in <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// Phones on gym wifi drop the websocket constantly and the client retries for minutes, so the
    /// circuit is held long enough that walking out of signal and back in recovers in place.
    /// </remarks>
    public static readonly TimeSpan DisconnectedCircuitRetention = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a lease survives without a heartbeat before it stops counting as busy.
    /// </summary>
    /// <remarks>
    /// <para><b>Derived, never hard-coded.</b> The invariant is that a lease must outlive every window
    /// in which its circuit could still come back, because a circuit that comes back resumes IN PLACE
    /// with its editor's unsaved state intact. Worst case, measured from the last successful
    /// heartbeat: up to one <see cref="HeartbeatInterval"/> passes before the client falls silent,
    /// then <see cref="ClientTimeout"/> before the hub notices and starts the retention clock, then
    /// <see cref="DisconnectedCircuitRetention"/> during which a reconnect is still accepted. The sum
    /// is the floor; this is that sum.</para>
    /// <para>The earlier two-minute value was shorter than the five-minute retention alone, so a
    /// backgrounded tablet lost its lease about two and a half minutes in while its circuit stayed
    /// recoverable for five. The autodeploy poll runs once a minute, so that gap was sampled two or
    /// three times and the container could be recreated under a user who was about to resume.</para>
    /// <para>Erring long is cheap: the only reader is the deploy gate, and
    /// <see cref="InactivityTimeToLive"/> is what bounds a lease nobody is using.</para>
    /// </remarks>
    public static readonly TimeSpan LeaseTimeToLive =
        DisconnectedCircuitRetention + ClientTimeout + HeartbeatInterval;

    /// <summary>
    /// How long a lease whose circuit is perfectly alive, but which no human has touched, still
    /// counts as work in flight.
    /// </summary>
    /// <remarks>
    /// <para>The heartbeat proves the SOCKET is alive, not that anyone is there. An editor left open
    /// on a gym tablet heartbeats forever and would pin the deploy gate exactly the way the original
    /// incident did, so <see cref="EditActivityEntry.LastActivityUtc"/> is tracked separately from
    /// <see cref="EditActivityEntry.LastSeenUtc"/> and expires on its own.</para>
    /// <para>Thirty minutes, chosen to sit between the two cases that matter: somebody reading a wall
    /// or staring at the alignment editor for ten minutes must keep their protection, and a tab
    /// abandoned overnight must not. It is also the deploy script's own <c>MAX_DEFER</c> window (30
    /// polls at one a minute), so an abandoned tab can no longer cost a deploy more than the script
    /// was already prepared to wait.</para>
    /// </remarks>
    public static readonly TimeSpan InactivityTimeToLive = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The absolute lifetime of a <see cref="EditKind.Maintenance"/> lease, measured from when the
    /// job started.
    /// </summary>
    /// <remarks>
    /// Maintenance is exempt from both TTLs above because nothing heartbeats it and nobody clicks in
    /// it — but "exempt" used to mean "immortal", which re-creates the original bug in miniature: a
    /// job whose work hangs on an HTTP or database call with no timeout holds the gate forever, and
    /// <c>ApplicationStopping</c> cannot rescue it because the deploy it is blocking is what would
    /// raise it. Six hours is far longer than any real job (the longest is a full image re-encode)
    /// and still finite.
    /// </remarks>
    public static readonly TimeSpan MaintenanceMaxLifetime = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether an entry has gone quiet for long enough to stop counting as in-flight work.
    /// </summary>
    public static bool IsExpired(EditActivityEntry entry, DateTimeOffset now)
    {
        if (entry.EditKind == EditKind.Maintenance)
        {
            // No circuit heartbeats it and no user clicks in it, so only the absolute cap applies.
            return now - entry.StartedUtc >= MaintenanceMaxLifetime;
        }

        return now - entry.LastSeenUtc >= LeaseTimeToLive
               || now - entry.LastActivityUtc >= InactivityTimeToLive;
    }

    /// <summary>
    /// How long until <paramref name="entry"/> would expire for want of a heartbeat; never negative.
    /// Reported by the busy health check so an incident says WHICH clock ran out.
    /// </summary>
    public static TimeSpan HeartbeatRemaining(EditActivityEntry entry, DateTimeOffset now)
    {
        return Remaining(LeaseTimeToLive - (now - entry.LastSeenUtc));
    }

    /// <summary>
    /// How long until <paramref name="entry"/> would expire for want of a human; never negative.
    /// </summary>
    public static TimeSpan ActivityRemaining(EditActivityEntry entry, DateTimeOffset now)
    {
        return Remaining(InactivityTimeToLive - (now - entry.LastActivityUtc));
    }

    /// <summary>
    /// How long until a <see cref="EditKind.Maintenance"/> entry hits its absolute cap; never
    /// negative.
    /// </summary>
    public static TimeSpan MaintenanceRemaining(EditActivityEntry entry, DateTimeOffset now)
    {
        return Remaining(MaintenanceMaxLifetime - (now - entry.StartedUtc));
    }

    private static TimeSpan Remaining(TimeSpan value)
    {
        return value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }
}
