// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>
/// The scan upload's limit counts per user and key (it runs after authentication), and a phone sending a whole package
/// about 20 files at a time, with retries, never reaches it.
/// </summary>
public class WallScanRateLimiterTests
{
    [Fact]
    public async Task TheBucket_IsPerUserAndKey_NotPerAddress()
    {
        using var limiter = new WallScanRateLimiter(burst: 2, perSecond: 1);

        Assert.True(await PassesAsync(limiter, Caller("gh|1", "key-a")));
        Assert.True(await PassesAsync(limiter, Caller("gh|1", "key-a")));
        Assert.False(await PassesAsync(limiter, Caller("gh|1", "key-a")));
        Assert.True(await PassesAsync(limiter, Caller("gh|1", "key-b")));
        Assert.True(await PassesAsync(limiter, Caller("gh|2", "key-a")));
        Assert.True(await PassesAsync(limiter, Caller("gh|3", null)));
    }

    [Fact]
    public async Task OnePhone_SendingAPackageTwentyAtATime_IsNeverLimited()
    {
        using var limiter = new WallScanRateLimiter();
        var phone = Caller("gh|1", "phone-key");

        // 150 rounds of 20 parallel PUTs, as fast as the filter takes them, plus one retry in ten.
        for (var round = 0; round < 150; round++)
        {
            var passed = await Task.WhenAll(Enumerable.Range(0, 22).Select(_ => PassesAsync(limiter, phone).AsTask()));
            Assert.All(passed, Assert.True);
        }
    }

    private static ClaimsPrincipal Caller(string user, string? key)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user) };
        if (key is not null)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.ApiKeyId, key));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static async ValueTask<bool> PassesAsync(WallScanRateLimiter limiter, ClaimsPrincipal user)
    {
        var http = new DefaultHttpContext { User = user };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        var result = await limiter.InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http), _ => ValueTask.FromResult<object?>(Results.Ok()));
        return result is not JsonHttpResult<ScanErrorResponse> { StatusCode: StatusCodes.Status429TooManyRequests };
    }
}
