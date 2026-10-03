// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Runners;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>A runner key names a machine: it must never authenticate a user anywhere, not even by reaching the JWT handler.</summary>
public class RunnerKeySurfaceTests
{
    [Theory]
    [InlineData("/api/runners/claim")]
    [InlineData("/api/walls/00000000-0000-0000-0000-000000000001/temperature")]
    [InlineData("/api/v1/me")]
    [InlineData("/walls")]
    public void RunnerKey_FallsThroughToTheCookieScheme_Everywhere(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers.Authorization = "Bearer " + GpuRunnerTokens.Create().Token;

        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, ApiKeySurface.SelectScheme(context));
    }
}
