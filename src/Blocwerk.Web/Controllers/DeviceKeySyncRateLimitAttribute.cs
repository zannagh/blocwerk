// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Counts a device key's read in the sync bucket it shares with the documents, assets and manifest
/// (<see cref="DeviceKeySyncRateLimiter"/>), by key id, and answers 429 with <c>Retry-After: 1</c> when it is empty. The
/// controller twin of <see cref="DeviceKeySyncRateLimit.RequireDeviceKeySyncRateLimit"/>; a caller that is not a device
/// key is left to <see cref="DeviceKeyOnlyAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DeviceKeySyncRateLimitAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        if (!http.User.IsDeviceKey() || http.User.GetApiKeyId() is not { } keyId)
        {
            return;
        }

        var limiter = http.RequestServices.GetRequiredService<DeviceKeySyncRateLimiter>();
        if (!limiter.TryAcquire(keyId))
        {
            http.Response.Headers.RetryAfter = "1";
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
        }
    }
}
