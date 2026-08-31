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
        if (!registry.IsBusy)
        {
            var idleData = new Dictionary<string, object>
            {
                ["busy"] = false,
                ["count"] = 0,
            };

            return Task.FromResult(HealthCheckResult.Healthy("Idle: no in-flight edits.", idleData));
        }

        var snapshot = registry.Snapshot();
        var details = snapshot
            .Select(e => new
            {
                kind = e.EditKind.ToString(),
                wallId = e.WallId,
                startedUtc = e.StartedUtc,
            })
            .ToList();

        var busyData = new Dictionary<string, object>
        {
            ["busy"] = true,
            ["count"] = snapshot.Count,
            ["details"] = details,
        };

        return Task.FromResult(HealthCheckResult.Degraded(
            $"Busy: {snapshot.Count} in-flight edit(s).", data: busyData));
    }
}
