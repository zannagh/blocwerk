// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Jobs;

namespace Blocwerk.Core.Tests;

/// <summary>The remaining-time math: from a measured rate, from a stage's history, never guessed.</summary>
public class JobEtaTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromRate_DividesWhatIsLeft_ByTheRateSinceTheAnchor()
    {
        // 6000 steps in 600 s = 10/s; 18000 left = 1800 s.
        Assert.Equal(1800, JobEta.FromRate(12000, 30000, 6000, Now.AddSeconds(-600), Now));

        // Resumed from a checkpoint: the rate counts only the steps of this claim.
        Assert.Equal(100, JobEta.FromRate(29000, 30000, 28000, Now.AddSeconds(-100), Now));
    }

    [Theory]
    [InlineData(6000, 6000, 600)] // no progress since the anchor
    [InlineData(6100, 6000, 5)] // measured over less than the minimum window
    public void FromRate_IsUnknown_WithoutAMeasurableRate(int step, int anchor, int secondsAgo)
    {
        Assert.Null(JobEta.FromRate(step, 30000, anchor, Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void FromRate_IsUnknown_WithoutStepsOrTotal()
    {
        Assert.Null(JobEta.FromRate(null, 30000, 0, Now.AddMinutes(-1), Now));
        Assert.Null(JobEta.FromRate(100, null, 0, Now.AddMinutes(-1), Now));
        Assert.Null(JobEta.FromRate(100, 30000, null, Now.AddMinutes(-1), Now));
        Assert.Null(JobEta.FromRate(100, 0, 0, Now.AddMinutes(-1), Now));
        Assert.Null(JobEta.FromRate(31000, 30000, 0, Now.AddMinutes(-1), Now));
    }

    [Fact]
    public void FromHistory_IsTheMedianLessTheElapsed_AndUnknownOnceOverrun()
    {
        Assert.Equal(240, JobEta.FromHistory(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));
        Assert.Equal(0, JobEta.FromHistory(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)));
        Assert.Null(JobEta.FromHistory(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(6)));
        Assert.Null(JobEta.FromHistory(null, TimeSpan.FromMinutes(1)));
        Assert.Null(JobEta.FromHistory(TimeSpan.FromMinutes(5), null));
    }

    [Fact]
    public void Median_NeedsEnoughSamples_AndAveragesTheMiddlePair()
    {
        Assert.Null(JobEta.Median([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]));
        Assert.Equal(TimeSpan.FromSeconds(20), JobEta.Median([TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)]));
        Assert.Equal(
            TimeSpan.FromSeconds(25),
            JobEta.Median([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(900)]));

        // A negative duration (clock skew) is no sample.
        Assert.Null(JobEta.Median([TimeSpan.FromSeconds(-5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]));
    }

    [Fact]
    public void Percent_ClampsAndRounds()
    {
        Assert.Equal(42.3, JobEta.Percent(0.42345));
        Assert.Equal(100, JobEta.Percent(1.7));
        Assert.Null(JobEta.Percent(double.NaN));
        Assert.Null(JobEta.Percent(null));
    }

    [Fact]
    public void History_KeysByKindAndStage()
    {
        var history = new JobStageHistory(
        [
            (JobKinds.Capture, "solving", TimeSpan.FromSeconds(30)),
            (JobKinds.Capture, "solving", TimeSpan.FromSeconds(40)),
            (JobKinds.Capture, "solving", TimeSpan.FromSeconds(50)),
            (JobKinds.Resolve, "solving", TimeSpan.FromSeconds(500)),
        ]);

        Assert.Equal(TimeSpan.FromSeconds(40), history.Median(JobKinds.Capture, "solving"));
        Assert.Null(history.Median(JobKinds.Resolve, "solving")); // one sample is not enough
        Assert.Null(history.Median(JobKinds.Capture, "texturing"));
    }
}
