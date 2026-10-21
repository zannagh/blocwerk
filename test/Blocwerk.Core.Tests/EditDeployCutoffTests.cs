// <copyright file="EditDeployCutoffTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>The day's cutoff after which no editing lease holds the deploy gate, across time zones and DST.</summary>
public class EditDeployCutoffTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    /// <summary>Summer: Berlin midnight is 22:00 UTC the evening before.</summary>
    [Fact]
    public void InSummerTheCutoffIsBerlinMidnightNotUtcMidnight()
    {
        Assert.Equal(Utc(2026, 7, 1, 22, 0), EditDeployCutoff.NextAfter(Utc(2026, 7, 1, 10, 0), Berlin, TimeSpan.Zero));

        // 23:30 UTC is already 01:30 the next day in Berlin, so the next midnight is a full day on.
        Assert.Equal(Utc(2026, 7, 2, 22, 0), EditDeployCutoff.NextAfter(Utc(2026, 7, 1, 23, 30), Berlin, TimeSpan.Zero));
    }

    /// <summary>Winter: Berlin midnight is 23:00 UTC.</summary>
    [Fact]
    public void InWinterTheCutoffIsAnHourLaterInUtc()
    {
        Assert.Equal(Utc(2026, 1, 15, 23, 0), EditDeployCutoff.NextAfter(Utc(2026, 1, 15, 9, 0), Berlin, TimeSpan.Zero));
    }

    /// <summary>A lease started on the cutoff instant itself runs to the NEXT cutoff, never zero.</summary>
    [Fact]
    public void ALeaseStartedExactlyAtTheCutoffGetsTheNextOne()
    {
        Assert.Equal(Utc(2026, 7, 2, 22, 0), EditDeployCutoff.NextAfter(Utc(2026, 7, 1, 22, 0), Berlin, TimeSpan.Zero));
    }

    /// <summary>The nights the clocks change: the cutoff is still local midnight, so the UTC offset flips with it.</summary>
    [Fact]
    public void TheDstSwitchNightsUseThatDaysOffset()
    {
        // Spring forward on Sunday 2026-03-29 at 02:00 CET. Saturday's midnight is still CET; Sunday's is CEST.
        Assert.Equal(Utc(2026, 3, 28, 23, 0), EditDeployCutoff.NextAfter(Utc(2026, 3, 28, 12, 0), Berlin, TimeSpan.Zero));
        Assert.Equal(Utc(2026, 3, 29, 22, 0), EditDeployCutoff.NextAfter(Utc(2026, 3, 29, 12, 0), Berlin, TimeSpan.Zero));

        // Fall back on Sunday 2026-10-25 at 03:00 CEST.
        Assert.Equal(Utc(2026, 10, 24, 22, 0), EditDeployCutoff.NextAfter(Utc(2026, 10, 24, 12, 0), Berlin, TimeSpan.Zero));
        Assert.Equal(Utc(2026, 10, 25, 23, 0), EditDeployCutoff.NextAfter(Utc(2026, 10, 25, 12, 0), Berlin, TimeSpan.Zero));
    }

    /// <summary>A configured time inside the spring-forward gap moves to the end of the gap.</summary>
    [Fact]
    public void ATimeThatDoesNotExistMovesToTheEndOfTheGap()
    {
        // 02:30 on 2026-03-29 does not exist in Berlin; 03:00 CEST is 01:00 UTC.
        var cutoff = EditDeployCutoff.NextAfter(Utc(2026, 3, 28, 12, 0), Berlin, new TimeSpan(2, 30, 0));
        Assert.Equal(Utc(2026, 3, 29, 1, 0), cutoff);
    }

    /// <summary>A configured time that happens twice takes the first occurrence.</summary>
    [Fact]
    public void ATimeThatHappensTwiceTakesTheFirst()
    {
        // 02:30 on 2026-10-25 happens at 00:30 UTC (CEST) and again at 01:30 UTC (CET).
        var cutoff = EditDeployCutoff.NextAfter(Utc(2026, 10, 24, 12, 0), Berlin, new TimeSpan(2, 30, 0));
        Assert.Equal(Utc(2026, 10, 25, 0, 30), cutoff);
    }

    /// <summary>The zone and the time of day are configuration; an unknown zone falls back to UTC.</summary>
    [Fact]
    public void TheZoneAndTimeAreConfigurable()
    {
        var tokyo = EditDeployCutoff.Zone(new EditPresenceOptions { CutoffTimeZone = "Asia/Tokyo" });
        Assert.Equal(Utc(2026, 7, 1, 21, 0), EditDeployCutoff.NextAfter(Utc(2026, 7, 1, 10, 0), tokyo, new TimeSpan(6, 0, 0)));

        var unknown = EditDeployCutoff.Zone(new EditPresenceOptions { CutoffTimeZone = "Not/AZone" });
        Assert.Equal(TimeZoneInfo.Utc, unknown);
        Assert.Equal(Utc(2026, 7, 2, 0, 0), EditDeployCutoff.NextAfter(Utc(2026, 7, 1, 10, 0), unknown, TimeSpan.Zero));
    }

    /// <summary>busy-eta's edit part: seconds to the latest cutoff of a lease that holds the gate, 0 for none.</summary>
    [Fact]
    public void BusyEtaReportsTheCutoffOfPresentEditsOnly()
    {
        var now = Utc(2026, 7, 1, 21, 0);
        var present = new EditActivityEntry(EditKind.BoulderCreate, null, null, now, now, now);
        var absent = new EditActivityEntry(EditKind.WallEdit, null, null, now - TimeSpan.FromHours(2), now, now - TimeSpan.FromHours(1));
        var options = new EditPresenceOptions();

        Assert.Equal(3600, EditDeployGatePolicy.SecondsUntilEditCutoff([present, absent], now, options));
        Assert.Equal(0, EditDeployGatePolicy.SecondsUntilEditCutoff([absent], now, options));
        Assert.Equal(0, EditDeployGatePolicy.SecondsUntilEditCutoff([], now, options));
    }

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);
}
