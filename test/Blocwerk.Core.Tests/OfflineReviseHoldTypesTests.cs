// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline revise request's hold types, as the JSON the queue replays: the set a current client sends, and the single
/// <c>holdType</c> a request queued by a 0.7.0 client still carries, which speaks for the grips only.
/// </summary>
public sealed class OfflineReviseHoldTypesTests
{
    private const BoulderHoldType Stored = BoulderHoldType.Crimp | BoulderHoldType.HeelHook;

    [Theory]
    [InlineData("""{"name":"B","holdTypes":["Pinch","Toe hook"]}""", BoulderHoldType.Pinch | BoulderHoldType.ToeHook)]
    [InlineData("""{"name":"B","holdTypes":[]}""", BoulderHoldType.None)]
    [InlineData("""{"name":"B","holdType":"Sloper"}""", BoulderHoldType.Sloper | BoulderHoldType.HeelHook)]
    [InlineData("""{"name":"B","holdType":""}""", BoulderHoldType.HeelHook)]
    [InlineData("""{"name":"B"}""", Stored)]
    [InlineData("""{"name":"B","holdType":"Bogus"}""", Stored)]
    public async Task TheRequestsHoldTypes_ReachTheRevision(string json, BoulderHoldType expected)
    {
        var request = JsonSerializer.Deserialize<ReviseBoulderRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        BoulderHoldTypeChange? sent = null;
        var boulders = Substitute.For<IBoulderService>();
        boulders.ReviseBoulderAsync(default, default, default, default, default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                sent = call.ArgAt<BoulderHoldTypeChange?>(11);
                return new Boulder { Name = "B" };
            });
        var currentUser = Substitute.For<ICurrentUserService>();

        await new OfflineBouldersController(boulders, currentUser).Revise(Guid.NewGuid(), request);

        Assert.Equal(expected, sent is { } change ? change.ApplyTo(Stored) : Stored);
    }
}
