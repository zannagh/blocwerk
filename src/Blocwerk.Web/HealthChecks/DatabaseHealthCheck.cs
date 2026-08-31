using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blocwerk.Web.HealthChecks;

/// <summary>
/// Verifies the app can reach its PostgreSQL database. Uses <see cref="IDbContextFactory{TContext}"/>
/// (the same factory the rest of the app uses) and a cheap <c>CanConnectAsync</c> probe. Reports
/// <see cref="HealthStatus.Unhealthy"/> — which drives the main <c>/health</c> endpoint to 503 — when
/// the connection can't be opened or the probe throws.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;

    public DatabaseHealthCheck(IDbContextFactory<BlocwerkDbContext> dbContextFactory)
    {
        this.dbContextFactory = dbContextFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var canConnect = await db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database cannot be reached.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database probe threw.", ex);
        }
    }
}
