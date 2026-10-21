using Blocwerk.Web.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Blocwerk.Web.HealthChecks;

/// <summary>
/// Reports whether any user is actively creating a boulder or editing a wall (see
/// <see cref="EditActivityRegistry"/>). Reports <see cref="HealthStatus.Degraded"/> (never
/// Unhealthy) while busy, so the main <c>/health</c> endpoint stays 200 during normal editing; the
/// deploy gate at <c>/health/ready-to-deploy</c> maps Degraded to 503 to hold a deploy back.
/// </summary>
/// <remarks>
/// An open editor counts only while its user is present (<see cref="EditDeployGatePolicy"/>): input within
/// <see cref="EditPresenceOptions.PresenceWindow"/>, the page not hidden for longer than
/// <see cref="EditPresenceOptions.HiddenGrace"/>, and the day's cutoff after the lease started not yet passed
/// (<see cref="EditDeployCutoff"/>).
/// Leases that do not hold the gate are still listed, with <c>holdsGate = false</c>, so an idle report says why.
/// </remarks>
public sealed class BusyHealthCheck(EditActivityRegistry registry, IOptions<EditPresenceOptions>? options = null) : IHealthCheck
{
    private readonly EditPresenceOptions presence = options?.Value ?? new EditPresenceOptions();

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // One clock for the whole check, so the count and the ages can never disagree.
        var now = DateTimeOffset.UtcNow;
        var snapshot = registry.Snapshot(now);
        var holding = snapshot.Count(e => EditDeployGatePolicy.HoldsGate(e, now, presence));

        // The clocks are reported separately, so the next incident says WHICH one was about to run out
        // rather than leaving it to be re-derived: idleSeconds is how long the circuit has not proved it
        // is connected, userIdleSeconds how long no human has touched the editor, hiddenSeconds how long
        // its page has been out of sight.
        var data = new Dictionary<string, object>
        {
            ["busy"] = holding > 0,
            ["count"] = holding,
        };

        if (snapshot.Count > 0)
        {
            data["leases"] = snapshot.Count;
            data["leaseTtlSeconds"] = (int)EditActivityPolicy.LeaseTimeToLive.TotalSeconds;
            data["inactivityTtlSeconds"] = (int)EditActivityPolicy.InactivityTimeToLive.TotalSeconds;
            data["presenceWindowSeconds"] = (int)presence.PresenceWindow.TotalSeconds;
            data["hiddenGraceSeconds"] = (int)presence.HiddenGrace.TotalSeconds;
            data["cutoffTimeZone"] = presence.CutoffTimeZone;
            data["cutoffTimeOfDay"] = presence.CutoffTimeOfDay.ToString("hh\\:mm", System.Globalization.CultureInfo.InvariantCulture);
            data["details"] = snapshot.Select(e => Describe(e, now)).ToList();
        }

        if (holding == 0)
        {
            var message = snapshot.Count == 0
                ? "Idle: no in-flight edits."
                : $"Idle: {snapshot.Count} open edit(s), none with a present user.";
            return Task.FromResult(HealthCheckResult.Healthy(message, data));
        }

        return Task.FromResult(HealthCheckResult.Degraded(
            $"Busy: {holding} in-flight edit(s).", data: data));
    }

    private object Describe(EditActivityEntry entry, DateTimeOffset now)
    {
        var (expiresIn, expiresBy) = EditDeployGatePolicy.Remaining(entry, now, presence);

        return new
        {
            kind = entry.EditKind.ToString(),
            wallId = entry.WallId,
            startedUtc = entry.StartedUtc,
            lastSeenUtc = entry.LastSeenUtc,
            lastActivityUtc = entry.LastActivityUtc,
            hiddenSinceUtc = entry.HiddenSinceUtc,
            cutoffUtc = EditActivityPolicy.IsBackgroundWork(entry.EditKind)
                ? (DateTimeOffset?)null
                : EditDeployCutoff.NextAfter(entry.StartedUtc, EditDeployCutoff.Zone(presence), presence.CutoffTimeOfDay),
            ageSeconds = (int)(now - entry.StartedUtc).TotalSeconds,
            idleSeconds = (int)(now - entry.LastSeenUtc).TotalSeconds,
            userIdleSeconds = (int)(now - entry.LastActivityUtc).TotalSeconds,
            hiddenSeconds = entry.HiddenSinceUtc is { } hidden ? (int)(now - hidden).TotalSeconds : 0,
            holdsGate = expiresIn > TimeSpan.Zero,
            expiresBy,
            expiresInSeconds = (int)expiresIn.TotalSeconds,
        };
    }
}
