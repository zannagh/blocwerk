using Blocwerk.Web.HealthChecks;
using Blocwerk.Web.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The busy signal's time-to-live. The incident this encodes: one <c>WallEdit</c> lease whose user
/// had long since walked away held <c>/health/ready-to-deploy</c> at <c>busy</c> for 95 minutes,
/// because the registry is in memory and only a restart clears it — while itself blocking the deploy
/// that would have restarted it.
/// </summary>
/// <remarks>
/// Everything here drives the clock through the <c>nowOverride</c> seam rather than sleeping, the
/// same way <see cref="KioskPairingTests"/> does: there is no time abstraction in this codebase and
/// a two-minute TTL is not something a test may wait out.
/// </remarks>
public class EditActivityLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 14, 2, 43, TimeSpan.Zero);
    private static readonly Guid Wall = Guid.NewGuid();

    /// <summary>The incident itself: nobody disposes the lease, and the gate must open anyway.</summary>
    [Fact]
    public void AnAbandonedLeaseExpiresAndFreesTheDeployGate()
    {
        var registry = new EditActivityRegistry();
        registry.Acquire(EditKind.WallEdit, Wall, userId: null, Now);

        Assert.True(registry.IsBusy(Now));
        Assert.True(registry.IsBusy(Now + EditActivityPolicy.LeaseTimeToLive - TimeSpan.FromSeconds(1)));

        var after = Now + EditActivityPolicy.LeaseTimeToLive;
        Assert.False(registry.IsBusy(after));
        Assert.Equal(0, registry.Count(after));
        Assert.Empty(registry.Snapshot(after));
    }

    /// <summary>A lease that is being touched is a lease whose circuit is alive.</summary>
    [Fact]
    public void ATouchedLeaseDoesNotExpireOnTheOriginalDeadline()
    {
        var registry = new EditActivityRegistry();
        var lease = registry.Acquire(EditKind.WallEdit, Wall, userId: null, Now);

        var touchedAt = Now + TimeSpan.FromSeconds(90);
        Assert.True(registry.Touch(lease.Id, touchedAt));

        // The original deadline passes and the lease is still counted...
        Assert.True(registry.IsBusy(Now + EditActivityPolicy.LeaseTimeToLive));

        // ...but the TTL is restarted, not removed.
        Assert.False(registry.IsBusy(touchedAt + EditActivityPolicy.LeaseTimeToLive));
    }

    /// <summary>
    /// Somebody who is still working keeps their protection indefinitely. The touches here carry
    /// <c>userActivity</c> on purpose: a bare heartbeat proves only that the socket is up, and since
    /// the inactivity TTL was added it can no longer hold the gate on its own — see
    /// <see cref="EditActivityLeasePolicyTests.AConnectedButUntouchedLeaseExpiresOnTheInactivityWindow"/>.
    /// </summary>
    [Fact]
    public void ALiveSessionKeepsTheGateClosedIndefinitely()
    {
        var registry = new EditActivityRegistry();
        var lease = registry.Acquire(EditKind.WallEdit, Wall, userId: null, Now);

        var clock = Now;
        for (var tick = 0; tick < 200; tick++)
        {
            clock += EditActivityPolicy.HeartbeatInterval;
            Assert.True(
                registry.Touch(lease.Id, clock, userActivity: true), $"heartbeat {tick} was refused");
            Assert.True(registry.IsBusy(clock));
        }

        // 100 minutes in — longer than the incident lasted — and still busy.
        Assert.True(clock - Now > TimeSpan.FromMinutes(95));
        Assert.True(registry.IsBusy(clock));

        // And the moment the session stops, the TTL runs out.
        Assert.False(registry.IsBusy(clock + EditActivityPolicy.LeaseTimeToLive));
    }

    /// <summary>
    /// A heartbeat that arrives after the deadline does not resurrect the entry: if it was that
    /// late, the circuit was not alive, and the deploy gate has already been told so.
    /// </summary>
    [Fact]
    public void ALateTouchDoesNotResurrectAnExpiredLease()
    {
        var registry = new EditActivityRegistry();
        var lease = registry.Acquire(EditKind.WallEdit, Wall, userId: null, Now);

        var late = Now + EditActivityPolicy.LeaseTimeToLive + TimeSpan.FromSeconds(1);
        Assert.False(registry.Touch(lease.Id, late));
        Assert.False(registry.IsBusy(late));
    }

    /// <summary>
    /// A maintenance job has no circuit, so nothing heartbeats its lease. Expiring it would open the
    /// deploy gate under a running job — exactly what the lease exists to prevent.
    /// </summary>
    [Fact]
    public void AMaintenanceLeaseIsExemptFromTheTimeToLive()
    {
        var registry = new EditActivityRegistry();
        var lease = registry.Acquire(EditKind.Maintenance, wallId: null, userId: null, Now);

        var muchLater = Now + TimeSpan.FromHours(3);
        Assert.True(registry.IsBusy(muchLater));
        Assert.Equal(1, registry.Count(muchLater));

        // Still released the ordinary way, by the runner's finally.
        lease.Dispose();
        Assert.False(registry.IsBusy(muchLater));
    }

    /// <summary>Expiry is checked on read; the entry is dropped on the next write.</summary>
    [Fact]
    public void AnExpiredEntryIsPrunedByTheNextWrite()
    {
        var registry = new EditActivityRegistry();
        registry.Acquire(EditKind.BoulderCreate, Wall, userId: null, Now);

        var later = Now + TimeSpan.FromMinutes(10);
        var survivor = registry.Acquire(EditKind.BoulderRevise, Wall, userId: null, later);

        var snapshot = registry.Snapshot(later);
        Assert.Equal(EditKind.BoulderRevise, Assert.Single(snapshot).EditKind);
        Assert.True(registry.Touch(survivor.Id, later));
    }

    /// <summary>The circuit wrapper touches every lease it is holding, and only while undisposed.</summary>
    [Fact]
    public void TheCircuitWrapperTouchesAllOfItsLeases()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null);
        activity.BeginBoulderCreate(Wall, userId: null);

        var heartbeat = Now + TimeSpan.FromSeconds(90);
        Assert.Equal(2, activity.Touch(heartbeat));
        Assert.Equal(2, registry.Count(heartbeat + TimeSpan.FromSeconds(30)));

        activity.Dispose();
        Assert.Equal(0, activity.Touch(heartbeat));
        Assert.False(registry.IsBusy(heartbeat));
    }

    /// <summary>The whole point, end to end: the gate the autodeploy hook polls opens again.</summary>
    [Fact]
    public async Task TheHealthCheckReportsHealthyOnceTheLeaseHasExpired()
    {
        var registry = new EditActivityRegistry();
        var live = registry.Acquire(EditKind.WallEdit, Wall, userId: null);
        var check = new BusyHealthCheck(registry);

        var busy = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Degraded, busy.Status);
        Assert.True((bool)busy.Data["busy"]);
        Assert.Equal(
            (int)EditActivityPolicy.LeaseTimeToLive.TotalSeconds,
            (int)busy.Data["leaseTtlSeconds"]);

        // The leaked lease, stamped as it would have been 95 minutes into the incident.
        live.Dispose();
        registry.Acquire(EditKind.WallEdit, Wall, userId: null, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(95));

        var idle = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, idle.Status);
        Assert.Equal(0, (int)idle.Data["count"]);
    }

    /// <summary>The details a future incident is diagnosed from, without repeating this investigation.</summary>
    [Fact]
    public async Task TheHealthCheckSurfacesTheLastSeenAgeOfEveryLease()
    {
        var registry = new EditActivityRegistry();
        registry.Acquire(EditKind.WallEdit, Wall, userId: null, DateTimeOffset.UtcNow - TimeSpan.FromSeconds(60));

        var result = await new BusyHealthCheck(registry).CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None);

        var detail = Assert.Single((IEnumerable<object>)result.Data["details"]);
        var fields = detail.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(detail));

        Assert.Equal(nameof(EditKind.WallEdit), fields["kind"]);
        Assert.Equal(Wall, fields["wallId"]);
        Assert.InRange((int)fields["idleSeconds"]!, 55, 70);
        Assert.InRange((int)fields["ageSeconds"]!, 55, 70);
        Assert.True((DateTimeOffset)fields["lastSeenUtc"]! <= DateTimeOffset.UtcNow);
    }
}
