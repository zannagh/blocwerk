using Blocwerk.Web.HealthChecks;
using Blocwerk.Web.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The three ways the busy signal can be wrong, each of which was a real finding: a TTL shorter than
/// the window in which a circuit can still resume; a reconnect that comes back with no protection at
/// all; and a lease whose socket is alive but whose human left hours ago.
/// </summary>
/// <remarks>
/// Split from <see cref="EditActivityLeaseTests"/> only to keep both files under the project's
/// file-size rule. Same approach: the clock is driven through the <c>nowOverride</c> seam, never
/// waited out.
/// </remarks>
public class EditActivityLeasePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 14, 2, 43, TimeSpan.Zero);
    private static readonly Guid Wall = Guid.NewGuid();

    /// <summary>
    /// F1: the lease must outlive every moment at which the circuit could still come back. A TTL
    /// shorter than the retention period let the deploy gate open under a user whose circuit was
    /// about to resume in place, and the autodeploy poll samples that gap two or three times.
    /// </summary>
    [Fact]
    public void TheLeaseOutlivesEveryMomentACircuitCouldStillResume()
    {
        Assert.True(
            EditActivityPolicy.LeaseTimeToLive > EditActivityPolicy.DisconnectedCircuitRetention,
            "a lease must not die while its circuit is still recoverable");

        // The worst case, measured from the last successful heartbeat.
        var lastReconnectOpportunity = EditActivityPolicy.HeartbeatInterval
                                       + EditActivityPolicy.ClientTimeout
                                       + EditActivityPolicy.DisconnectedCircuitRetention;

        Assert.True(EditActivityPolicy.LeaseTimeToLive >= lastReconnectOpportunity);

        var registry = new EditActivityRegistry();
        registry.Acquire(EditKind.WallEdit, Wall, userId: null, Now);
        Assert.True(registry.IsBusy(Now + lastReconnectOpportunity - TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// F2: every editing component acquires once, in its initialisation. A reconnect after the TTL
    /// therefore used to leave the user editing with no protection at all, and invisible to the
    /// health check.
    /// </summary>
    [Fact]
    public void AReconnectRestoresProtectionAfterTheLeaseHasExpired()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);

        var late = Now + EditActivityPolicy.LeaseTimeToLive + TimeSpan.FromSeconds(30);
        Assert.False(registry.IsBusy(late));

        // The connection coming back up is the proof the client is there.
        Assert.Equal(1, activity.Resume(late));
        Assert.True(registry.IsBusy(late));
        Assert.Equal(EditKind.WallEdit, Assert.Single(registry.Snapshot(late)).EditKind);

        // And the restored lease is a normal lease: it keeps its history and expires on schedule.
        Assert.True(registry.IsBusy(late + EditActivityPolicy.LeaseTimeToLive - TimeSpan.FromSeconds(1)));
        Assert.False(registry.IsBusy(late + EditActivityPolicy.LeaseTimeToLive));
    }

    /// <summary>
    /// The half of F2 that must NOT be given away: a heartbeat is the one signal an abandoned tab
    /// produces by itself, so it may stamp a live lease but never bring one back.
    /// </summary>
    [Fact]
    public void AHeartbeatAloneCannotBringAnExpiredLeaseBack()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);

        var late = Now + EditActivityPolicy.LeaseTimeToLive;
        Assert.Equal(0, activity.Touch(late));
        Assert.False(registry.IsBusy(late));
    }

    /// <summary>
    /// F5: the heartbeat proves the socket is alive, not that anyone is there. A tab left open on a
    /// gym tablet heartbeats forever, which is the shape the original 95-minute leak most likely had.
    /// </summary>
    [Fact]
    public void AConnectedButUntouchedLeaseExpiresOnTheInactivityWindow()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);

        var clock = Now;
        var deadline = Now + EditActivityPolicy.InactivityTimeToLive;
        while (clock + EditActivityPolicy.HeartbeatInterval < deadline)
        {
            clock += EditActivityPolicy.HeartbeatInterval;
            Assert.Equal(1, activity.Touch(clock));
            Assert.True(registry.IsBusy(clock));
        }

        // The socket never died; nobody ever did anything. The gate opens anyway.
        Assert.False(registry.IsBusy(deadline));
        Assert.Equal(0, activity.Touch(deadline));

        // And a reconnect does not hand it back, because the entry's own activity history is what
        // the restore is re-checked against.
        Assert.Equal(0, activity.Resume(deadline));
        Assert.False(registry.IsBusy(deadline));
    }

    /// <summary>Somebody actually working keeps their protection for as long as they work.</summary>
    [Fact]
    public void RealUserActivityKeepsTheGateClosedPastTheInactivityWindow()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginBoulderCreate(Wall, userId: null, Now);

        var clock = Now;
        for (var tick = 0; tick < 24; tick++)
        {
            // One interaction every five minutes: a slow, thoughtful setter, not a script.
            clock += TimeSpan.FromMinutes(5);
            Assert.Equal(1, activity.RecordActivity(clock));
            Assert.True(registry.IsBusy(clock));
        }

        Assert.True(clock - Now > TimeSpan.FromHours(1));

        // Stop working, and both clocks run out on schedule.
        Assert.False(registry.IsBusy(clock + EditActivityPolicy.InactivityTimeToLive));
    }

    /// <summary>
    /// A user who comes back to the tab after the inactivity window is protected again the moment
    /// they touch anything — it is the abandoned tab that must lose the lease, not the returning one.
    /// </summary>
    [Fact]
    public void ReturningToAnIdleTabTakesTheProtectionBack()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginBoulderRevise(Wall, userId: null, Now);

        var backAt = Now + EditActivityPolicy.InactivityTimeToLive + TimeSpan.FromMinutes(20);
        Assert.False(registry.IsBusy(backAt));

        Assert.Equal(1, activity.RecordActivity(backAt));
        Assert.True(registry.IsBusy(backAt));
    }

    /// <summary>
    /// F8: a maintenance job whose work hangs held an immortal lease — the original bug in miniature,
    /// because the deploy it blocks is what would have raised <c>ApplicationStopping</c>.
    /// </summary>
    [Fact]
    public void AMaintenanceLeaseCannotOutliveItsAbsoluteCap()
    {
        var registry = new EditActivityRegistry();
        registry.Acquire(EditKind.Maintenance, wallId: null, userId: null, Now);

        // Nothing touches it for the whole run, which is exactly how maintenance is meant to work.
        Assert.True(registry.IsBusy(Now + EditActivityPolicy.MaintenanceMaxLifetime - TimeSpan.FromMinutes(1)));
        Assert.False(registry.IsBusy(Now + EditActivityPolicy.MaintenanceMaxLifetime));
    }

    /// <summary>Both clocks reach the health check, so the next incident says which one ran out.</summary>
    [Fact]
    public async Task TheHealthCheckReportsLivenessAndUserActivitySeparately()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        var opened = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(27);
        activity.BeginWallEdit(Wall, userId: null, opened);

        // Connected the whole time and untouched by anyone for twenty-seven minutes: the abandoned-tab
        // shape, which only the heartbeat keeps alive.
        for (var clock = opened; clock < DateTimeOffset.UtcNow; clock += EditActivityPolicy.HeartbeatInterval)
        {
            activity.Touch(clock);
        }

        activity.Touch(DateTimeOffset.UtcNow);

        var result = await new BusyHealthCheck(registry).CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(
            (int)EditActivityPolicy.InactivityTimeToLive.TotalSeconds,
            (int)result.Data["inactivityTtlSeconds"]);

        var detail = Assert.Single((IEnumerable<object>)result.Data["details"]);
        var fields = detail.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(detail));

        Assert.InRange((int)fields["idleSeconds"]!, 0, 5);
        Assert.InRange((int)fields["userIdleSeconds"]!, 1615, 1630);

        // Both clocks are running; the report names the one that will run out first. Here that is
        // the inactivity window, which is the whole point of tracking it separately.
        Assert.Equal("inactivity", fields["expiresBy"]);
        Assert.InRange((int)fields["expiresInSeconds"]!, 170, 190);
    }
}
