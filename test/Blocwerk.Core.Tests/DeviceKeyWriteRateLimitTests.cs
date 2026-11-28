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
/// The device writes' bucket: one per key id (burst <see cref="DeviceKeyWriteRateLimiter.DefaultBurst"/>, refilled
/// <see cref="DeviceKeyWriteRateLimiter.DefaultPerSecond"/> a second), so one key running dry never touches another,
/// and an empty bucket answers 429 with Retry-After.
/// </summary>
public sealed class DeviceKeyWriteRateLimitTests
{
    [Fact]
    public void TheDefaults_AreSizedForNormalLogging()
    {
        Assert.Equal(60, DeviceKeyWriteRateLimiter.DefaultBurst);
        Assert.Equal(1, DeviceKeyWriteRateLimiter.DefaultPerSecond);
    }

    [Fact]
    public void AnEmptyBucket_Answers429_ForThatKeyOnly()
    {
        using var limiter = new DeviceKeyWriteRateLimiter(burst: 2, perPeriod: 1, period: TimeSpan.FromHours(1));
        var first = DeviceKeyTestPrincipals.Device();
        var second = DeviceKeyTestPrincipals.Device();

        Assert.Null(Filter(limiter, first, out _));
        Assert.Null(Filter(limiter, first, out _));
        var refused = Assert.IsType<StatusCodeResult>(Filter(limiter, first, out var http));
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        Assert.Equal("1", http.Response.Headers.RetryAfter.ToString());

        Assert.Null(Filter(limiter, second, out _));
    }

    [Fact]
    public void ACallerThatIsNoDeviceKey_IsNotCounted()
    {
        using var limiter = new DeviceKeyWriteRateLimiter(burst: 1, perPeriod: 1, period: TimeSpan.FromHours(1));

        Assert.Null(Filter(limiter, DeviceKeyTestPrincipals.Plain(), out _));
        Assert.Null(Filter(limiter, DeviceKeyTestPrincipals.Plain(), out _));
    }

    private static IActionResult? Filter(DeviceKeyWriteRateLimiter limiter, ClaimsPrincipal user, out HttpContext http)
    {
        http = new DefaultHttpContext
        {
            User = user,
            RequestServices = new ServiceCollection().AddSingleton(limiter).BuildServiceProvider(),
        };
        var action = new ActionContext(http, new RouteData(), new ControllerActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), controller: new object());
        new DeviceKeyWriteRateLimitAttribute().OnActionExecuting(context);
        return context.Result;
    }
}
