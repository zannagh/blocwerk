// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>The pulse interceptor on a migrated Postgres database (<see cref="WallPulseInterceptorScenarios"/>).</summary>
public sealed class PostgresWallPulseInterceptorTests
{
    [PostgresFact]
    public Task ACaptureSave_PulsesAfterItIsSaved() => RunAsync(WallPulseInterceptorScenarios.ACaptureSave_PulsesAfterItIsSaved);

    [PostgresFact]
    public Task InATransaction_OnlyTheCommitPulses_AndARollbackNothing() =>
        RunAsync(WallPulseInterceptorScenarios.InATransaction_OnlyTheCommitPulses_AndARollbackNothing);

    [PostgresFact]
    public Task EveryJobRow_PulsesItsTopic_AndOtherRowsNothing() =>
        RunAsync(WallPulseInterceptorScenarios.EveryJobRow_PulsesItsTopic_AndOtherRowsNothing);

    [PostgresFact]
    public Task TheFollowUpRecordsBulkWrite_PulsesTheCapture() =>
        RunAsync(WallPulseInterceptorScenarios.TheFollowUpRecordsBulkWrite_PulsesTheCapture);

    private static async Task RunAsync(Func<WallTestHarness, Task> scenario)
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        await h.SeedWallAsync(1);
        await scenario(h);
    }
}
