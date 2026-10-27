// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>The panel registration job's wire format: the capability, the result the server accepts, and what it refuses.</summary>
public class RunnerPanelPosesTests
{
    private static readonly Guid Panel = Guid.NewGuid();

    [Fact]
    public void Parse_ReadsThePoseAsARadialCamera()
    {
        var pose = Assert.Single(RunnerPanelPoses.Parse(Doc(Panel.ToString("N") + "r3", "[1578.2, 2016, 1512, 0.0014, -0.0012]")));

        Assert.Equal(Panel, pose.Key);
        Assert.Equal(new double[] { 1578.2, 0, 2016, 0, 1578.2, 1512, 0, 0, 1 }, pose.Value.Camera.K);
        Assert.Equal(new[] { 0.0014, -0.0012, 0, 0 }, pose.Value.Camera.Dist);
        Assert.Equal((3, 5397, 5.35), (pose.Value.PhotoRevision, pose.Value.Observations, pose.Value.RmsPx));
    }

    [Theory]
    [InlineData("not-a-panel", "[1, 2, 3, 0, 0]")]
    [InlineData("PANEL", "[1, 2, 3, 0]")]
    [InlineData("NOREVISION", "[1, 2, 3, 0, 0]")]
    public void Parse_RefusesAnUnknownPanelOrABadCamera(string name, string parameters)
    {
        var doc = Doc(name switch { "PANEL" => Panel.ToString("N") + "r0", "NOREVISION" => Panel.ToString("N"), _ => name }, parameters);

        Assert.Throws<InvalidDataException>(() => RunnerPanelPoses.Parse(doc));
    }

    [Fact]
    public void Capability_OffersPanelJobsOnlyToRunnersThatAdvertiseIt()
    {
        Assert.True(RunnerCapabilities.Allows("splat,register-panels", GpuJobKind.RegisterPanels));
        Assert.False(RunnerCapabilities.Allows("splat,detect-holds", GpuJobKind.RegisterPanels));
        Assert.False(RunnerCapabilities.Allows(null, GpuJobKind.RegisterPanels));
        Assert.Equal("register-panels", RunnerCapabilities.KindName(GpuJobKind.RegisterPanels));
    }

    private static JsonElement Doc(string name, string parameters) => JsonDocument.Parse($$"""
        { "version": 1, "panels": { "{{name}}": { "width": 4032, "height": 3024, "params": {{parameters}},
          "R": [1, 0, 0, 0, 1, 0, 0, 0, 1], "t": [0, 0, 3000], "observations": 5397, "rmsPx": 5.35, "medianPx": 3.63 } } }
        """).RootElement;
}
