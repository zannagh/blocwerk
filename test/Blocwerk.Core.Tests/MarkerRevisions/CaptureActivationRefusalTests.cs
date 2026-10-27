// <copyright file="CaptureActivationRefusalTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests.MarkerRevisions;

/// <summary>A model refused by the sanity gate AND the registration names both reasons, not just the gate's.</summary>
public class CaptureActivationRefusalTests
{
    private const string Integrity = "The new solve could not fit 10 of the 49 unchanged markers (0, 4, 10, 11, 13, 15, 16, 17, 18, 19).";

    [Fact]
    public void BothRefusals_AreNamed()
    {
        var message = CaptureActivationRefusal.Message(["its photos reproject 5.0 px off"], Integrity);

        Assert.StartsWith("The solved model failed the sanity checks: its photos reproject 5.0 px off.", message);
        Assert.Contains("It could not be tied to the active model either: " + Integrity, message);
        Assert.Contains("saved but NOT activated", message);
    }

    [Fact]
    public void TheGateAlone_OrTheRegistrationAlone_ReadAsBefore()
    {
        var gate = CaptureActivationRefusal.Message(["its photos reproject 5.0 px off"], null);
        var registration = CaptureActivationRefusal.Message([], Integrity);
        var unexplained = CaptureActivationRefusal.Message([], null);

        Assert.DoesNotContain("tied to the active model", gate);
        Assert.Contains("re-capture the parts they name", gate);
        Assert.StartsWith(Integrity + " The new 3D model was saved but NOT activated", registration);
        Assert.StartsWith("The new 3D model was saved but NOT activated", unexplained);
    }
}
