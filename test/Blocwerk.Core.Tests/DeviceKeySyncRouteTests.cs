// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Web.Endpoints;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline client's sync routes through their real registered endpoints, called with a Bearer key and no cookie: a
/// linked device key reads them as its owner, under the owner's own grant; a plain personal key and a wall key still get
/// the bare 404; and a device key whose owner may not see the wall is refused like that owner's cookie.
/// </summary>
public sealed class DeviceKeySyncRouteTests
{
    [Fact]
    public async Task DocumentIndexAndDocuments_AdmitADeviceKey_RefuseAPlainKey_AndAWallTheOwnerCannotSee()
    {
        using var f = new DataDocumentFixture();
        await f.SeedAsync();
        f.Principal = DeviceKeyTestPrincipals.Device();

        var index = await f.IndexHttpAsync();
        Assert.Equal(StatusCodes.Status200OK, index.Response.StatusCode);
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);
        Assert.Equal(StatusCodes.Status200OK, (await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha)).Response.StatusCode);

        f.Principal = DeviceKeyTestPrincipals.Plain();
        Assert.Equal(StatusCodes.Status404NotFound, (await f.IndexHttpAsync()).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha)).Response.StatusCode);
        f.Principal = DeviceKeyTestPrincipals.Wall();
        Assert.Equal(StatusCodes.Status404NotFound, (await f.IndexHttpAsync()).Response.StatusCode);

        f.Principal = DeviceKeyTestPrincipals.Device();
        f.Caller = f.Member;
        Assert.Equal(StatusCodes.Status403Forbidden, (await f.IndexHttpAsync(wallId: f.OtherWallId)).Response.StatusCode);
    }

    [Fact]
    public async Task Assets_AdmitADeviceKey_RefuseAPlainKey_AndAWallTheOwnerCannotSee()
    {
        using var f = new AssetRouteFixture();
        var seeded = await f.SeedAsync();

        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, seeded.Panel, configure: AsDevice)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await f.GetAsync(f.Harness.WallId, seeded.Panel, 640, configure: AsDevice)).Response.StatusCode);

        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetAsync(f.Harness.WallId, seeded.Panel, configure: AsPlain)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetAsync(f.Harness.WallId, seeded.Panel, 640, configure: AsPlain)).Response.StatusCode);

        f.Caller = new User { Identifier = "outsider@test" };
        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetAsync(f.Harness.WallId, seeded.Panel, configure: AsDevice)).Response.StatusCode);
    }

    [Fact]
    public async Task TheManifest_AdmitsADeviceKey_RefusesAPlainKey_AndAWallTheOwnerCannotSee()
    {
        using var f = new ManifestFixture();
        await f.SeedAsync();

        Assert.Equal(StatusCodes.Status200OK, (await f.GetHttpAsync(configure: AsDevice)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetHttpAsync(configure: AsPlain)).Response.StatusCode);

        f.Assets.Caller = new User { Identifier = "outsider@test" };
        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetHttpAsync(configure: AsDevice)).Response.StatusCode);
    }

    [Fact]
    public async Task WallChanges_AdmitADeviceKey_AsItsOwnersRole_RefuseAPlainKey_AndAWallTheOwnerCannotSee()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var beginning = "?after=" + await f.BeginningAsync();
        f.Principal = DeviceKeyTestPrincipals.Device();

        var owner = FeedRouteFixture.Changes(await f.WallChangesAsync(beginning));
        f.Caller = f.Member;
        var member = FeedRouteFixture.Changes(await f.WallChangesAsync(beginning));
        Assert.True(owner.Events.Count > member.Events.Count); // the owner's audiences, not a flat key view

        f.Principal = DeviceKeyTestPrincipals.Plain();
        Assert.Equal(StatusCodes.Status404NotFound, (await f.WallChangesAsync(beginning)).Response.StatusCode);

        f.Principal = DeviceKeyTestPrincipals.Device();
        f.Caller = new User { Identifier = "outsider@test" };
        FeedRouteFixture.AssertRefused(await f.WallChangesAsync(beginning), 403, FeedRefusals.NoAccess);
    }

    /// <summary>The stream with only a Bearer key: no cookie, no antiforgery token (a GET never needs one).</summary>
    [Fact]
    public async Task TheStream_OpensForADeviceKey_AndRefusesAPlainKey()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.Principal = DeviceKeyTestPrincipals.Device();
        await using (var run = FeedStreamRun.Open(f, "?after=" + await f.BeginningAsync()))
        {
            await run.Body.WaitUntilAsync(b => b.Events.Count == 4);
            await f.CommitAsync(true, f.Row("live"));
            await run.Body.WaitUntilAsync(b => b.Events.Count == 5);
        }

        f.Principal = DeviceKeyTestPrincipals.Plain();
        var refused = await f.GetAsync(FeedEndpoints.StreamRoute, f.WallId, "?after=" + await f.BeginningAsync());
        Assert.Equal(StatusCodes.Status404NotFound, refused.Response.StatusCode);
    }

    [Fact]
    public async Task OwnChanges_AtTheV1Alias_AdmitADeviceKey_AndRefuseAPlainKey()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var beginning = "?after=" + await f.BeginningAsync();
        f.Principal = DeviceKeyTestPrincipals.Device();

        var own = FeedRouteFixture.Changes(await f.GetAsync(FeedEndpoints.DeviceSelfChangesRoute, query: beginning));
        Assert.Contains(own.Events, e => e.Kind == "owner.mine");

        f.Principal = DeviceKeyTestPrincipals.Plain();
        Assert.Equal(StatusCodes.Status404NotFound, (await f.GetAsync(FeedEndpoints.DeviceSelfChangesRoute, query: beginning)).Response.StatusCode);
    }

    [Fact]
    public async Task Mine_AtTheV1Alias_AdmitsADeviceKey_RefusesAPlainKey_AndAWallTheOwnerCannotSee()
    {
        using var f = new MineDocumentFixture();
        await f.SeedAsync();
        f.Docs.Caller = f.Docs.Member;
        f.Docs.Principal = DeviceKeyTestPrincipals.Device();

        var mine = await f.HttpAsync(route: MineEndpoints.DeviceRoute);
        Assert.Equal(StatusCodes.Status200OK, mine.Response.StatusCode);
        Assert.Equal(4, MineDocumentFixture.Parse(mine).Ticks.Count);

        f.Docs.Principal = DeviceKeyTestPrincipals.Plain();
        Assert.Equal(StatusCodes.Status404NotFound, (await f.HttpAsync(route: MineEndpoints.DeviceRoute)).Response.StatusCode);

        f.Docs.Principal = DeviceKeyTestPrincipals.Device();
        f.Docs.Caller = f.Outsider;
        var outsider = await f.HttpAsync(route: MineEndpoints.DeviceRoute);
        Assert.Equal(StatusCodes.Status403Forbidden, outsider.Response.StatusCode);
        Assert.DoesNotContain("ticks", MineDocumentFixture.Body(outsider));
    }

    private static void AsDevice(HttpContext http) => http.User = DeviceKeyTestPrincipals.Device();

    private static void AsPlain(HttpContext http) => http.User = DeviceKeyTestPrincipals.Plain();
}
