// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The controller twin of the sync reads' bucket: a device read on a controller route spends the same per-key bucket as
/// the documents and images, and an empty one answers 429 with Retry-After for that key only.
/// </summary>
public sealed class DeviceKeySyncRateLimitAttributeTests
{
    [Fact]
    public void AnEmptyBucket_Answers429_ForThatKeyOnly_AndOtherCallersAreNotCounted()
    {
        using var limiter = new DeviceKeySyncRateLimiter(burst: 1, perPeriod: 1, period: TimeSpan.FromHours(1));
        var spender = DeviceKeyTestPrincipals.Device();

        Assert.Null(Filter(limiter, spender, out _));
        var refused = Assert.IsType<StatusCodeResult>(Filter(limiter, spender, out var http));
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        Assert.Equal("1", http.Response.Headers.RetryAfter.ToString());

        Assert.Null(Filter(limiter, DeviceKeyTestPrincipals.Device(), out _));
        Assert.Null(Filter(limiter, DeviceKeyTestPrincipals.Plain(), out _));
        Assert.Null(Filter(limiter, DeviceKeyTestPrincipals.Plain(), out _));
    }

    private static IActionResult? Filter(DeviceKeySyncRateLimiter limiter, ClaimsPrincipal user, out HttpContext http)
    {
        http = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddSingleton(limiter).BuildServiceProvider(),
        };
        var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller: new object());
        new DeviceKeySyncRateLimitAttribute().OnActionExecuting(context);
        return context.Result;
    }
}
