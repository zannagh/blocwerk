namespace Blocwerk.Core.Services;

/// <summary>
/// Supplies the app-wide administration dashboard with a global overview of walls, users, boulders
/// and recent usage. Reads across every wall, bypassing the per-user wall query filter.
/// </summary>
public interface IAdminDashboardService
{
    /// <summary>
    /// Builds the global overview: total walls/users/boulders and a per-wall breakdown of members,
    /// boulders and recent load.
    /// </summary>
    Task<AdminOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
}
