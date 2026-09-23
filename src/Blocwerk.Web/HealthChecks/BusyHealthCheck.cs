using Blocwerk.Web.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blocwerk.Web.HealthChecks;

/// <summary>
/// Reports whether any user is actively creating a boulder or editing a wall (see
/// <see cref="EditActivityRegistry"/>). Reports <see cref="HealthStatus.Degraded"/> (never
/// Unhealthy) while busy, so the main <c>/health</c> endpoint stays 200 during normal editing; the
/// deploy gate at <c>/health/ready-to-deploy</c> maps Degraded to 503 to hold a deploy back.
/// </summary>
public sealed class BusyHealthCheck : IHealthCheck
{
    private readonly EditActivityRegistry registry;

    public BusyHealthCheck(EditActivityRegistry registry)
    {
        this.registry = registry;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // One clock for the whole check, so the count and the ages can never disagree.
        var now = DateTimeOffset.UtcNow;
        var snapshot = registry.Snapshot(now);

        if (snapshot.Count == 0)
        {
            var idleData = new Dictionary<string, object>
            {
                ["busy"] = false,
                ["count"] = 0,
            };

            return Task.FromResult(HealthCheckResult.Healthy("Idle: no in-flight edits.", idleData));
        }

        // Two clocks, reported separately, so the next incident says WHICH one was about to run out
        // rather than leaving it to be re-derived: idleSeconds is how long the circuit has not
        // proved it is connected, userIdleSeconds how long no human has done anything in it. A
        // Maintenance entry has neither — nothing heartbeats it and nobody clicks in it — so it is
        // measured against its absolute cap instead, and its idle seconds simply equal its age.
        var details = snapshot.Select(e => Describe(e, now)).ToList();

        var busyData = new Dictionary<string, object>
        {
            ["busy"] = true,
            ["count"] = snapshot.Count,
            ["leaseTtlSeconds"] = (int)EditActivityPolicy.LeaseTimeToLive.TotalSeconds,
            ["inactivityTtlSeconds"] = (int)EditActivityPolicy.InactivityTimeToLive.TotalSeconds,
            ["details"] = details,
        };

        return Task.FromResult(HealthCheckResult.Degraded(
            $"Busy: {snapshot.Count} in-flight edit(s).", data: busyData));
    }

    private static object Describe(EditActivityEntry entry, DateTimeOffset now)
    {
        var maintenance = EditActivityPolicy.IsBackgroundWork(entry.EditKind);
        var heartbeat = EditActivityPolicy.HeartbeatRemaining(entry, now);
        var activity = EditActivityPolicy.ActivityRemaining(entry, now);

        var expiresIn = maintenance
            ? EditActivityPolicy.MaintenanceRemaining(entry, now)
            : (heartbeat <= activity ? heartbeat : activity);

        var expiresBy = maintenance
            ? "maintenanceCap"
            : (heartbeat <= activity ? "heartbeat" : "inactivity");

        return new
        {
            kind = entry.EditKind.ToString(),
            wallId = entry.WallId,
            startedUtc = entry.StartedUtc,
            lastSeenUtc = entry.LastSeenUtc,
            lastActivityUtc = entry.LastActivityUtc,
            ageSeconds = (int)(now - entry.StartedUtc).TotalSeconds,
            idleSeconds = (int)(now - entry.LastSeenUtc).TotalSeconds,
            userIdleSeconds = (int)(now - entry.LastActivityUtc).TotalSeconds,
            expiresBy,
            expiresInSeconds = (int)expiresIn.TotalSeconds,
        };
    }
}
