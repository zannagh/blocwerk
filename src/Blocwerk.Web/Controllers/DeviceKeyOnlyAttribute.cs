// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The companion app's controller posture behind the <see cref="BlocwerkPolicies.DeviceApiKey"/> policy: anything but a
/// linked device key gets a bare 404 (the policy already refuses it; this keeps the answer the same if the policy is ever
/// widened), and every answer is <c>no-store</c>, because it is one person's data.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class DeviceKeyOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        if (!http.User.IsDeviceKey())
        {
            context.Result = new NotFoundResult();
        }
    }
}
