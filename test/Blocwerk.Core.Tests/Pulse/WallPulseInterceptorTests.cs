// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>The pulse interceptor on SQLite (<see cref="WallPulseInterceptorScenarios"/>).</summary>
public sealed class WallPulseInterceptorTests
{
    [Fact]
    public Task ACaptureSave_PulsesAfterItIsSaved() => RunAsync(WallPulseInterceptorScenarios.ACaptureSave_PulsesAfterItIsSaved);

    [Fact]
    public Task InATransaction_OnlyTheCommitPulses_AndARollbackNothing() =>
        RunAsync(WallPulseInterceptorScenarios.InATransaction_OnlyTheCommitPulses_AndARollbackNothing);

    [Fact]
    public Task EveryJobRow_PulsesItsTopic_AndOtherRowsNothing() =>
        RunAsync(WallPulseInterceptorScenarios.EveryJobRow_PulsesItsTopic_AndOtherRowsNothing);

    [Fact]
    public Task TheFollowUpRecordsBulkWrite_PulsesTheCapture() =>
        RunAsync(WallPulseInterceptorScenarios.TheFollowUpRecordsBulkWrite_PulsesTheCapture);

    private static async Task RunAsync(Func<WallTestHarness, Task> scenario)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(1);
        await scenario(h);
    }
}
