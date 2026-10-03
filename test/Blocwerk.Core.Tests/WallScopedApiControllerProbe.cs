// <copyright file="WallScopedApiControllerProbe.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>Exposes the protected wall guards of <see cref="WallScopedApiController"/> for one caller.</summary>
internal sealed class WallScopedApiControllerProbe : WallScopedApiController
{
    public WallScopedApiControllerProbe(ClaimsPrincipal user)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
    }

    /// <summary>The wall-admin guard's verdict for <paramref name="wallId"/>: null when admitted.</summary>
    public IActionResult? Guard(Guid wallId) => GuardWallOrPersonalKey(wallId);
}
