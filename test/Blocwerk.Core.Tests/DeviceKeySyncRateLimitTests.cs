// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The device keys' bucket on the sync reads, through the real document index route: a key that spends it gets a bare
/// 429 with <c>Retry-After</c>, another key and a cookie caller do not notice.
/// </summary>
public sealed class DeviceKeySyncRateLimitTests
{
    [Fact]
    public async Task ADeviceKeyOverItsBucket_Gets429_AnotherKeyAndACookieDoNot()
    {
        using var f = new DataDocumentFixture { SyncLimiter = new DeviceKeySyncRateLimiter(burst: 3, perPeriod: 1, period: TimeSpan.FromHours(1)) };
        await f.SeedAsync();
        var spender = DeviceKeyTestPrincipals.Device();
        f.Principal = spender;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(StatusCodes.Status200OK, (await f.IndexHttpAsync()).Response.StatusCode);
        }

        var refused = await f.IndexHttpAsync();
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.Response.StatusCode);
        Assert.Equal("1", refused.Response.Headers.RetryAfter.ToString());

        f.Principal = DeviceKeyTestPrincipals.Device();
        Assert.Equal(StatusCodes.Status200OK, (await f.IndexHttpAsync()).Response.StatusCode);

        f.Principal = null;
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(StatusCodes.Status200OK, (await f.IndexHttpAsync()).Response.StatusCode);
        }
    }
}
