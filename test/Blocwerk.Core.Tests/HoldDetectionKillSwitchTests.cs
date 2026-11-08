// <copyright file="HoldDetectionKillSwitchTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Detection.Gating;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>HoldDetection:Enabled=false</c>: photos get no detected holds and no enrichment, without touching the models.
/// </summary>
public class HoldDetectionKillSwitchTests
{
    [Fact]
    public void Bind_DefaultsToOn_AndReadsAnExplicitOff()
    {
        Assert.True(HoldDetectionSettings.Bind(Section([])).Enabled);
        Assert.False(HoldDetectionSettings.Bind(Section(new() { ["HoldDetection:Enabled"] = "false" })).Enabled);
    }

    [Fact]
    public async Task SwitchedOff_DetectionReturnsNoHolds_WithoutRunningTheDetector()
    {
        var inner = Substitute.For<IHoldDetectionService>();
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMinutes(1), NullLogger.Instance);
        var detection = new GatedHoldDetectionService(inner, gate, new HoldDetectionSettings { Enabled = false }, NullLogger.Instance);

        var holds = await detection.DetectHoldsAsync([1, 2, 3]);

        Assert.Empty(holds);
        await inner.DidNotReceiveWithAnyArgs().DetectHoldsAsync(default!, default);
    }

    [Fact]
    public async Task SwitchedOn_DetectionRunsTheDetector()
    {
        var inner = Substitute.For<IHoldDetectionService>();
        inner.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(Task.FromResult(new List<DetectedHold> { new(0.5, 0.5, 0.02, null, 0.9) }));
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMinutes(1), NullLogger.Instance);
        var detection = new GatedHoldDetectionService(inner, gate, new HoldDetectionSettings(), NullLogger.Instance);

        Assert.Single(await detection.DetectHoldsAsync([1, 2, 3]));
    }

    [Fact]
    public async Task SwitchedOff_EnrichmentSkips_WithoutOpeningAnOutlineSession()
    {
        var outlines = EnrichmentFakes.Outlines();
        var settings = new BlocwerkSettings();
        settings.HoldDetection.Enabled = false;
        var service = new HoldEnrichmentService(settings, NullLogger<HoldEnrichmentService>.Instance, outlines, EnrichmentFakes.Markers());
        var wall = new Wall { Name = "w" };
        var hold = EnrichmentFakes.AutoHold(wall.Id, 0.5, 0.5);

        var summary = await service.EnrichAsync(null!, new HoldEnrichmentRequest([1, 2, 3], wall, [hold]));

        Assert.Equal(HoldEnrichmentSummary.None, summary);
        Assert.Null(hold.ShapePoints);
        outlines.DidNotReceiveWithAnyArgs().OpenSession(default!);
    }

    private static IConfiguration Section(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
