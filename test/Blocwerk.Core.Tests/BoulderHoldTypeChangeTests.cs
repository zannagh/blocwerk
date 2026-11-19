// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What a revise request says about a boulder's hold types: the set (absent leaves it, empty clears it), or the single
/// type a 0.7.0 client still sends, which speaks for the grips only.
/// </summary>
public sealed class BoulderHoldTypeChangeTests
{
    private const BoulderHoldType Stored = BoulderHoldType.Crimp | BoulderHoldType.Pinch | BoulderHoldType.HeelHook;

    [Fact]
    public void NoField_LeavesTheSetAlone() => Assert.Null(BoulderHoldTypeChange.FromRequest(null, null));

    [Fact]
    public void AnEmptyList_ClearsEveryType() =>
        Assert.Equal(BoulderHoldType.None, BoulderHoldTypeChange.FromRequest([], null)!.Value.ApplyTo(Stored));

    [Fact]
    public void AList_ReplacesTheWholeSet() =>
        Assert.Equal(
            BoulderHoldType.Jug | BoulderHoldType.ToeHook,
            BoulderHoldTypeChange.FromRequest(["Jug", "toe-hook"], null)!.Value.ApplyTo(Stored));

    [Fact]
    public void AListOfOnlyUnknownNames_LeavesTheSetAlone() => Assert.Null(BoulderHoldTypeChange.FromRequest(["Bogus"], null));

    [Fact]
    public void TheList_WinsOverTheOldField() =>
        Assert.Equal(BoulderHoldType.Pocket, BoulderHoldTypeChange.FromRequest(["Pocket"], "Crimp")!.Value.ApplyTo(Stored));

    [Theory]
    [InlineData("Sloper", BoulderHoldType.Sloper | BoulderHoldType.HeelHook)]
    [InlineData("pinch", BoulderHoldType.Pinch | BoulderHoldType.HeelHook)]
    [InlineData("", BoulderHoldType.HeelHook)]
    public void TheOldSingleField_ReplacesTheGrips_AndKeepsTheHooks(string value, BoulderHoldType expected) =>
        Assert.Equal(expected, BoulderHoldTypeChange.FromRequest(null, value)!.Value.ApplyTo(Stored));

    [Fact]
    public void AStale070Replay_OfTheGripItWasShown_ChangesNothing()
    {
        // A 0.7.0 page showed the set's first grip (Crimp here) and sends it back on every save, changed or not.
        Assert.Equal(Stored, BoulderHoldTypeChange.FromRequest(null, "Crimp")!.Value.ApplyTo(Stored));
        Assert.Equal(
            BoulderHoldType.Crimp | BoulderHoldType.Pinch,
            BoulderHoldTypeChange.FromRequest(null, "crimp")!.Value.ApplyTo(BoulderHoldType.Crimp | BoulderHoldType.Pinch));
    }

    [Theory]
    [InlineData(BoulderHoldType.None)]
    [InlineData(BoulderHoldType.HeelHook | BoulderHoldType.ToeHook)]
    public void AStale070Replay_OfNoType_ChangesNothing_WhenTheSetHasNoGrip(BoulderHoldType current) =>
        Assert.Equal(current, BoulderHoldTypeChange.FromRequest(null, string.Empty)!.Value.ApplyTo(current));

    [Theory]
    [InlineData("bogus")]
    [InlineData("Heel hook")]
    public void AnOldFieldNamingNoGrip_LeavesTheSetAlone(string value) => Assert.Null(BoulderHoldTypeChange.FromRequest(null, value));

    [Fact]
    public void AFullChange_SpeaksForEveryType() =>
        Assert.Equal(BoulderHoldType.ToeHook, new BoulderHoldTypeChange(BoulderHoldType.ToeHook).ApplyTo(Stored));
}
