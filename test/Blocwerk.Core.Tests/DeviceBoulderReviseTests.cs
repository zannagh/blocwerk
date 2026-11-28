// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.DeviceSetterFixture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>PUT /api/v1/walls/{w}/boulders/{b}</c> for the companion app, with bodies as JSON: a field left out keeps the
/// boulder's value (setters, rule flags, foot colour, a draft's holds), the creator or an admin revises, an old retry
/// never rolls back a newer revision, and every bad body is a permanent 400.
/// </summary>
public sealed class DeviceBoulderReviseTests
{
    [Fact]
    public async Task APartialSave_ChangesOnlyWhatItSends()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var setter = await h.AddMemberAsync("setter@test", WallRole.Member);
        var id = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, change: r =>
        {
            r.IsDraft = true;
            r.Setters = [setter.Id];
            r.NoMatch = true;
            r.KickboardFootholdsOn = false;
            r.HandsFollowFeet = false;
            r.FootColorOnly = "red";
            r.CustomRule = "No dyno";
            r.Styles = 1;
        }))));

        Applied(await Controller(h).Revise(h.WallId, id, Json($$"""{ "clientRequestId": "{{Key()}}", "name": "Renamed" }""")));

        // An explicit null is the same as leaving the field out: the app sends "setters": null for "unchanged".
        Applied(await Controller(h).Revise(h.WallId, id, Json($$"""{ "clientRequestId": "{{Key()}}", "setters": null, "customRule": null }""")));

        await using var db = h.CreateContext();
        var b = await db.Boulders.Include(x => x.BoulderHolds).Include(x => x.Setters).SingleAsync(x => x.Id == id);
        Assert.Equal("Renamed", b.Name);
        Assert.Equal("6B+", b.Grade);
        Assert.Equal([setter.Id], b.Setters.Select(s => s.UserId));
        Assert.True(b.NoMatch);
        Assert.False(b.KickboardFootholdsOn);
        Assert.False(b.HandsFollowFeet);
        Assert.Equal("red", b.FootColorOnly);
        Assert.Equal("No dyno", b.CustomRule);
        Assert.Equal(1, (int)b.Styles);
        Assert.Equal(2, b.BoulderHolds.Count);
        Assert.True(b.IsDraft);
    }

    [Fact]
    public async Task ClearingIsExplicit()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var id = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds, change: r =>
        {
            r.Setters = [h.Owner.Id];
            r.FootColorOnly = "red";
            r.CustomRule = "No dyno";
        }))));

        Applied(await Controller(h).Revise(h.WallId, id, Json($$"""
            { "clientRequestId": "{{Key()}}", "setters": [], "footColorOnly": "", "customRule": "", "noMatch": true }
            """)));

        await using var db = h.CreateContext();
        var b = await db.Boulders.Include(x => x.Setters).SingleAsync(x => x.Id == id);
        Assert.Empty(b.Setters);
        Assert.Null(b.FootColorOnly);
        Assert.Null(b.CustomRule);
        Assert.True(b.NoMatch);
    }

    [Fact]
    public async Task TheCreator_Revises_AnotherMemberCannot_AndAnOldRetryNeverRollsBack()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var id = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds))));
        var first = Key();

        Applied(await Controller(h).Revise(h.WallId, id, Json($$"""{ "clientRequestId": "{{first}}", "name": "Renamed", "grade": "7A" }""")));
        Applied(await Controller(h).Revise(h.WallId, id, Json($$"""{ "clientRequestId": "{{Key()}}", "name": "Newer" }""")));
        Applied(await Controller(h).Revise(h.WallId, id, Json($$"""{ "clientRequestId": "{{first}}", "name": "Renamed", "grade": "7A" }""")));
        await using (var db = h.CreateContext())
        {
            Assert.Equal(("Newer", "7A"), await db.Boulders.Where(b => b.Id == id).Select(b => ValueTuple.Create(b.Name, b.Grade!)).SingleAsync());
        }

        Refused(await Controller(h).Revise(Guid.NewGuid(), id, Json($$"""{ "clientRequestId": "{{Key()}}", "name": "x" }""")), 404);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Refused(await Controller(h).Revise(h.WallId, id, Json($$"""{ "clientRequestId": "{{Key()}}", "name": "Mine now" }""")), 403);
    }

    [Theory]
    [InlineData("""{ "name": "No key" }""")]
    [InlineData("""{ "clientRequestId": "00000000-0000-0000-0000-000000000000", "name": "Empty key" }""")]
    [InlineData("""{ "clientRequestId": "KEY", "name": " " }""")]
    [InlineData("""{ "clientRequestId": "KEY", "grade": "6Z" }""")]
    [InlineData("""{ "clientRequestId": "KEY", "footColorOnly": "0123456789012345678901234567890123" }""")]
    [InlineData("""{ "clientRequestId": "KEY", "holds": [] }""")]
    [InlineData("""{ "clientRequestId": "KEY", "holdTypes": [ "Nope" ] }""")]
    [InlineData("""{ "clientRequestId": "KEY", "holdTypes": [ "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp", "Crimp" ] }""")]
    [InlineData("""{ "clientRequestId": "KEY", "styles": 1073741824 }""")]
    [InlineData("""{ "clientRequestId": "KEY", "holds": [ { "holdId": "HOLD" }, { "holdId": "HOLD" } ] }""")]
    public async Task ABadBody_IsAPermanent400(string json)
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync();
        var id = BoulderId(Applied(await Controller(h).Create(h.WallId, Create(holds))));

        var body = Json(json.Replace("KEY", Key()).Replace("HOLD", holds[0].Id.ToString()));
        Refused(await Controller(h).Revise(h.WallId, id, body), 400);
    }
}
