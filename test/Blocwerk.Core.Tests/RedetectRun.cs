// <copyright file="RedetectRun.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Redetect;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Builds the panel re-detect for a test wall and runs it in one go (plan, then apply, as the confirm does).</summary>
internal static class RedetectRun
{
    public static PanelRedetectService Service(WallTestHarness h, IHoldEnrichmentService? enrichment = null) => new(
        h.DbContextFactory, h.CurrentUser, h.HoldDetection, new HoldSamenessOptions(), NullLogger<PanelRedetectService>.Instance, enrichment);

    public static async Task<PanelRedetectSummary> RunAsync(this IPanelRedetectService service, Guid wallId, Guid panelId) =>
        await service.ApplyAsync(await service.PlanAsync(wallId, panelId));

    /// <summary>The stub detector finds these holds on every photo.</summary>
    public static void Detects(WallTestHarness h, params DetectedHold[] holds)
    {
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(holds.ToList()));
    }
}
