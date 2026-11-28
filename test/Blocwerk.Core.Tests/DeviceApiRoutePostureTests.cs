// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The companion app's own routes under <c>/api/v1</c>: each one admits a linked device key only (the
/// <see cref="BlocwerkPolicies.DeviceApiKey"/> policy, which <see cref="DeviceKeySyncPolicyTests"/> shows refuses a plain
/// personal key, a wall key and a cookie), carries <see cref="DeviceKeyOnlyAttribute"/> (404 for any other caller, and
/// <c>no-store</c>), and is rate limited like the feed.
/// </summary>
public sealed class DeviceApiRoutePostureTests
{
    /// <summary>Every device-only controller route, as the route table spells it. A new one is added here on purpose.</summary>
    public static readonly IReadOnlyList<(string Method, string Route)> DeviceOnlyRoutes =
    [
        ("POST", "api/v1/me/ratings"),
        ("DELETE", "api/v1/me/ratings/{boulderId:guid}"),
        ("POST", "api/v1/me/favorites"),
        ("DELETE", "api/v1/me/favorites/{boulderId:guid}"),
        ("POST", "api/v1/me/projects"),
        ("DELETE", "api/v1/me/projects/{boulderId:guid}"),
        ("POST", "api/v1/me/comments"),
        ("DELETE", "api/v1/me/comments/{commentId:guid}"),
        ("GET", "api/v1/me/profile"),
        ("PATCH", "api/v1/me/profile"),
        ("GET", "api/v1/users/{userId:guid}"),
        ("GET", "api/v1/walls/{wallId:guid}/members"),
        ("GET", "api/v1/walls/{wallId:guid}/hold-links"),
        ("POST", "api/v1/walls/{wallId:guid}/invite-link"),
        ("POST", "api/v1/walls/{wallId:guid}/invite-link/rotate"),
        ("POST", "api/v1/walls/{wallId:guid}/visit-links"),
        ("POST", "api/v1/join/{token}"),
        ("GET", "api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}/attempts"),
        ("POST", "api/v1/walls/{wallId:guid}/gallery"),
        ("DELETE", "api/v1/walls/{wallId:guid}/gallery/{imageId:guid}"),
        ("GET", "api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}/beta-videos"),
        ("POST", "api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}/beta-videos"),
        ("DELETE", "api/v1/walls/{wallId:guid}/boulders/{boulderId:guid}/beta-videos/{videoId:guid}"),
        ("GET", "api/v1/beta-videos/{videoId:guid}"),
        ("GET", "api/v1/beta-videos/{videoId:guid}/thumbnail"),
        ("GET", "api/v1/beta-videos/{videoId:guid}/hls/{*path}"),
    ];

    public static TheoryData<string, string> Routes()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, route) in DeviceOnlyRoutes)
        {
            data.Add(method, route);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void EachRoute_IsForADeviceKeyOnly_RateLimited_AndNeverCached(string method, string route)
    {
        var endpoint = Assert.Single(ApiSurfaceAuthorizationTests.AllEndpoints().Where(e =>
            e.RoutePattern.RawText == route
            && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true));

        Assert.True(ApiKeySurface.Covers(new PathString("/" + route)));
        Assert.Equal(BlocwerkPolicies.DeviceApiKey, endpoint.Metadata.GetMetadata<IAuthorizeData>()?.Policy);
        Assert.NotNull(endpoint.Metadata.GetMetadata<DeviceKeyOnlyAttribute>());

        // A read takes the key's sync bucket (or, for the older routes, the feed's); a write takes the key's own write
        // bucket and not the per-address one.
        if (method == "GET")
        {
            Assert.True(
                endpoint.Metadata.GetMetadata<DeviceKeySyncRateLimitAttribute>() is not null
                || endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == FeedEndpoints.RateLimitPolicy);
            Assert.Null(endpoint.Metadata.GetMetadata<DeviceKeyWriteRateLimitAttribute>());
        }
        else
        {
            Assert.Null(endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>());
            Assert.NotNull(endpoint.Metadata.GetMetadata<DeviceKeyWriteRateLimitAttribute>());
        }
    }

    [Fact]
    public void TheFilter_Answers404ToAPlainKeyAWallKeyAndACookie_AndNoStoreToEveryone()
    {
        Assert.IsType<NotFoundResult>(Filter(DeviceKeyTestPrincipals.Plain(), out var plain));
        Assert.IsType<NotFoundResult>(Filter(DeviceKeyTestPrincipals.Wall(), out _));
        Assert.IsType<NotFoundResult>(Filter(FeedRouteFixture.SignedIn(), out _));
        Assert.Null(Filter(DeviceKeyTestPrincipals.Device(), out var device));
        Assert.Equal("no-store", plain.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-store", device.Response.Headers.CacheControl.ToString());
    }

    private static IActionResult? Filter(System.Security.Claims.ClaimsPrincipal user, out HttpContext http)
    {
        http = new DefaultHttpContext { User = user };
        var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller: new object());
        new DeviceKeyOnlyAttribute().OnActionExecuting(context);
        return context.Result;
    }
}
