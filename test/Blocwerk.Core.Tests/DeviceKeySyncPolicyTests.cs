// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Feed;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The policies, the filter and the caller flag that open the sync routes to a linked device key and to no other key:
/// <see cref="BlocwerkPolicies.WallSync"/>, <see cref="BlocwerkPolicies.DeviceApiKey"/>, <see cref="DenyNonDeviceApiKeyFilter"/>
/// and <see cref="FeedCaller.IsRefusedKey"/>. The gallery-image policy itself still refuses every key.
/// </summary>
public sealed class DeviceKeySyncPolicyTests
{
    [Fact]
    public async Task TheSyncPolicy_AdmitsADeviceKey_AHumanAndAShareToken_ButNoOtherKey()
    {
        var policy = DeviceKeyPolicies.BuildWallSync(new AuthorizationPolicyBuilder());

        Assert.True(await AllowsAsync(policy, DeviceKeyTestPrincipals.Device()));
        Assert.True(await AllowsAsync(policy, FeedRouteFixture.SignedIn()));
        Assert.True(await AllowsAsync(policy, new ClaimsPrincipal(new ClaimsIdentity()), shareToken: "t"));
        Assert.False(await AllowsAsync(policy, new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.False(await AllowsAsync(policy, DeviceKeyTestPrincipals.Plain()));
        Assert.False(await AllowsAsync(policy, DeviceKeyTestPrincipals.Plain(), shareToken: "t"));
        Assert.False(await AllowsAsync(policy, DeviceKeyTestPrincipals.Wall()));
    }

    [Fact]
    public async Task TheGalleryImagePolicy_StillRefusesADeviceKey()
    {
        var policy = AuthenticationServices.BuildGalleryImagePolicy(new AuthorizationPolicyBuilder());

        Assert.False(await AllowsAsync(policy, DeviceKeyTestPrincipals.Device()));
        Assert.True(await AllowsAsync(policy, FeedRouteFixture.SignedIn()));
    }

    [Fact]
    public async Task TheDeviceKeyPolicy_AdmitsADeviceKeyOnly()
    {
        var policy = DeviceKeyPolicies.BuildDeviceApiKey(new AuthorizationPolicyBuilder());

        Assert.True(await AllowsAsync(policy, DeviceKeyTestPrincipals.Device()));
        Assert.False(await AllowsAsync(policy, DeviceKeyTestPrincipals.Plain()));
        Assert.False(await AllowsAsync(policy, DeviceKeyTestPrincipals.Wall()));
        Assert.False(await AllowsAsync(policy, FeedRouteFixture.SignedIn()));
    }

    [Fact]
    public async Task TheFilter_LetsADeviceKeyAndAHumanThrough_AndAnswersEveryOtherKey404()
    {
        Assert.Equal("next", await FilterAsync(DeviceKeyTestPrincipals.Device()));
        Assert.Equal("next", await FilterAsync(FeedRouteFixture.SignedIn()));
        Assert.Equal("next", await FilterAsync(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await FilterAsync(DeviceKeyTestPrincipals.Plain())).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await FilterAsync(DeviceKeyTestPrincipals.Wall())).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await FilterAsync(FeedRouteFixture.ApiKey())).StatusCode);
    }

    [Fact]
    public void TheCallerFlag_RefusesEveryKeyButADeviceKey()
    {
        Assert.False(new FeedCaller(true, IsApiKey: false).IsRefusedKey);
        Assert.True(new FeedCaller(true, IsApiKey: true).IsRefusedKey);
        Assert.False(new FeedCaller(true, IsApiKey: true, IsDeviceKey: true).IsRefusedKey);
    }

    [Fact]
    public void ADeviceClaimOnAWallKey_DoesNotMakeItADeviceKey()
    {
        var wall = DeviceKeyTestPrincipals.Wall();
        ((ClaimsIdentity)wall.Identity!).AddClaim(new Claim(ApiKeyClaimTypes.Device, "true"));

        Assert.True(wall.IsNonDeviceApiKey());
        Assert.False(DeviceKeyTestPrincipals.Device().IsNonDeviceApiKey());
        Assert.False(FeedRouteFixture.SignedIn().IsNonDeviceApiKey());
    }

    private static async Task<bool> AllowsAsync(AuthorizationPolicy policy, ClaimsPrincipal user, string? shareToken = null)
    {
        var request = new DefaultHttpContext { User = user };
        if (shareToken is not null)
        {
            request.Request.QueryString = new QueryString("?token=" + shareToken);
        }

        var services = new ServiceCollection().AddLogging().AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, WallGalleryImageHandler>();
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(request);
        services.AddSingleton(accessor);
        var authorization = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(user, request, policy)).Succeeded;
    }

    private static async Task<object?> FilterAsync(ClaimsPrincipal user)
    {
        var context = EndpointFilterInvocationContext.Create(new DefaultHttpContext { User = user });
        return await new DenyNonDeviceApiKeyFilter().InvokeAsync(context, _ => ValueTask.FromResult<object?>("next"));
    }
}
