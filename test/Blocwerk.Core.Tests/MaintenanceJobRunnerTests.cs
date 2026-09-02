using Blocwerk.Web.HealthChecks;
using Blocwerk.Web.Maintenance;
using Blocwerk.Web.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The maintenance job slot. What matters operationally is that a run holds the deploy gate: the
/// autodeploy hook polls <c>/health/ready-to-deploy</c> and would otherwise recreate the container
/// halfway through a job.
/// </summary>
public class MaintenanceJobRunnerTests
{
    [Fact]
    public async Task ARunningJobHoldsTheDeployGateAtBusy()
    {
        var registry = new EditActivityRegistry();
        var runner = Runner(registry);
        var release = new TaskCompletionSource();

        Assert.Equal(HealthStatus.Healthy, await StatusAsync(registry));

        Assert.True(runner.TryStart("test", async (_, _, _) =>
        {
            await release.Task;
            return "done";
        }));

        // TryStart has returned, so the gate must ALREADY be closed — no window in between.
        Assert.True(registry.IsBusy);
        Assert.Equal(HealthStatus.Degraded, await StatusAsync(registry));

        release.SetResult();
        await WaitForIdleAsync(runner);

        Assert.False(registry.IsBusy);
        Assert.Equal(HealthStatus.Healthy, await StatusAsync(registry));
        Assert.Equal("done", runner.Snapshot().Summary);
    }

    [Fact]
    public async Task OnlyOneJobRunsAtATime()
    {
        var registry = new EditActivityRegistry();
        var runner = Runner(registry);
        var release = new TaskCompletionSource();

        Assert.True(runner.TryStart("first", async (_, _, _) =>
        {
            await release.Task;
            return "first done";
        }));
        Assert.False(runner.TryStart("second", (_, _, _) => Task.FromResult("second done")));

        release.SetResult();
        await WaitForIdleAsync(runner);

        Assert.Equal("first done", runner.Snapshot().Summary);
    }

    /// <summary>A job that throws must release the gate, not wedge it closed until a restart.</summary>
    [Fact]
    public async Task AFailedJobStillReleasesTheDeployGate()
    {
        var registry = new EditActivityRegistry();
        var runner = Runner(registry);

        Assert.True(runner.TryStart("boom", (_, _, _) => Task.FromException<string>(new InvalidOperationException("boom"))));
        await WaitForIdleAsync(runner);

        Assert.False(registry.IsBusy);
        Assert.Equal("boom", runner.Snapshot().Error);
    }

    private static MaintenanceJobRunner Runner(EditActivityRegistry registry) =>
        new(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            registry,
            new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance),
            NullLogger<MaintenanceJobRunner>.Instance);

    private static async Task<HealthStatus> StatusAsync(EditActivityRegistry registry)
    {
        var result = await new BusyHealthCheck(registry).CheckHealthAsync(
            new HealthCheckContext(), CancellationToken.None);

        return result.Status;
    }

    private static async Task WaitForIdleAsync(MaintenanceJobRunner runner)
    {
        for (var i = 0; i < 200 && runner.IsRunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(runner.IsRunning, "the job did not finish in time");
    }
}
