// <copyright file="CaptureCoverageScaleTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Capture.Coverage;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The coverage analysis of an attic-sized capture (several facets, volumes with flat sides, hundreds of solved
/// cameras) stays within seconds: the report is computed on a background thread but must not run for minutes.
/// </summary>
public class CaptureCoverageScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void AnAtticSizedCapture_IsAnalysedInSeconds()
    {
        var inputs = AtticCoverageFixture.Inputs(545);
        var clock = Stopwatch.StartNew();

        var report = CaptureCoverageAnalyzer.Analyze(inputs, DateTimeOffset.UnixEpoch);

        output.WriteLine($"Analysis of {inputs.Photos.Count} cameras: {clock.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(3, report.Facets.Count);
        Assert.Equal(10, report.Volumes.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed.TotalSeconds:F1} s");
    }
}
