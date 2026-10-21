// <copyright file="EditFlushDisconnectedTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Web.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A setter's locked phone: the circuit is retained with the unsaved boulder in it, but its browser answers nothing.
/// The server-side save must happen and be reported, bounded, and the browser-only extras must not hold it up.
/// </summary>
public class EditFlushDisconnectedTests
{
    /// <summary>
    /// The shape every server-saving editor follows (BoulderCreate's draft, the wall editor's batch): save first,
    /// then tell the browser through <see cref="DeployDraftInterop.TellSavedAsync"/>. With the browser away the save
    /// still lands, the flush reports it saved, and the whole thing takes about one interop timeout.
    /// </summary>
    [Fact]
    public async Task AServerSaveIsReportedForACircuitWhoseBrowserIsAway()
    {
        var hub = new EditFlushHub(NullLogger<EditFlushHub>.Instance);
        using var circuit = new CircuitEditFlush(hub);
        var js = new HangingJsRuntime();
        var saved = 0;
        circuit.Register(
            work => work(),
            async () =>
            {
                saved++;
                await DeployDraftInterop.TellSavedAsync(js, "boulder-create:w", "saved", "/walls/w", TimeSpan.FromMilliseconds(200));
                return EditFlushOutcome.SavedOnServer;
            },
            () => new EditFlushTarget("boulder create", null, null));

        var clock = Stopwatch.StartNew();
        var report = await hub.FlushAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, saved);
        Assert.Equal(1, report.SavedOnServer);
        Assert.Equal(0, report.TimedOut);
        Assert.Equal(2, js.Calls);
        Assert.InRange(clock.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>What needs the browser (a copy kept on the device) is reported as not saved, never as success.</summary>
    [Fact]
    public async Task KeepingOnTheDeviceFailsHonestlyWhenTheBrowserIsAway()
    {
        var kept = await DeployDraftInterop.KeepAsync(new HangingJsRuntime(), "boulder-revise:b", "{}", "kept");
        Assert.False(kept);
    }

    [Fact]
    public void TheDefaultsAreValid()
    {
        Assert.True(new EditPresenceOptionsValidator().Validate(null, new EditPresenceOptions()).Succeeded);
    }

    /// <summary>A 0 (or any too-small value) in appsettings cannot quietly switch the gate off.</summary>
    [Theory]
    [InlineData("PresenceWindow", "00:00:00")]
    [InlineData("PresenceWindow", "00:10:00")]
    [InlineData("CutoffTimeOfDay", "1.00:00:00")]
    [InlineData("CutoffTimeOfDay", "-00:01:00")]
    [InlineData("CutoffTimeZone", "Not/AZone")]
    [InlineData("CutoffTimeZone", "")]
    [InlineData("FlushTimeout", "00:00:00")]
    [InlineData("FlushTimeout", "00:05:00")]
    public void ValuesThatWouldDisableOrBreakTheGateAreRefused(string option, string value)
    {
        var options = new EditPresenceOptions();
        var property = typeof(EditPresenceOptions).GetProperty(option)!;
        property.SetValue(options, property.PropertyType == typeof(TimeSpan) ? TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : value);

        var result = new EditPresenceOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(option, result.FailureMessage);
    }

    /// <summary>The presence window outlasts the lease, so a circuit that resumes in place finds its user still present.</summary>
    [Fact]
    public void ThePresenceWindowMustOutlastTheLease()
    {
        var justEnough = new EditPresenceOptions
        {
            PresenceWindow = EditActivityPolicy.LeaseTimeToLive + EditPresenceOptionsValidator.PresenceMargin,
        };
        Assert.True(new EditPresenceOptionsValidator().Validate(null, justEnough).Succeeded);

        var tooShort = new EditPresenceOptions { PresenceWindow = justEnough.PresenceWindow - TimeSpan.FromSeconds(1) };
        Assert.True(new EditPresenceOptionsValidator().Validate(null, tooShort).Failed);
    }
}
