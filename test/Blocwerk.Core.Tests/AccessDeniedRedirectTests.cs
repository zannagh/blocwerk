// <copyright file="AccessDeniedRedirectTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Authentication;
using Blocwerk.Authentication.Kiosk;
using Blocwerk.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A signed-in non-admin forbidden a page (e.g. /administration) used to be sent to the framework
/// default /Account/AccessDenied, which no route serves — a 404 with an empty body. These pin the
/// configured path, that a page actually serves it, and that a kiosk session may reach it.
/// </summary>
public class AccessDeniedRedirectTests
{
    [Fact]
    public async Task Forbid_RedirectsASignedInHumanToTheAccessDeniedPage()
    {
        await using var services = BuildAuthenticationServices();
        await using var scope = services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("blocwerk.example");
        context.Request.Path = "/administration";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme));

        await context.ForbidAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(StatusCodes.Status302Found, context.Response.StatusCode);
        var location = new Uri(context.Response.Headers.Location.ToString());
        Assert.Equal("/access-denied", location.AbsolutePath);
    }

    [Fact]
    public void AccessDeniedPath_IsServedByABlazorPage()
    {
        var templates = typeof(Blocwerk.Web.Program).Assembly
            .GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Cast<RouteAttribute>())
            .Select(r => r.Template);

        Assert.Contains(AuthenticationServices.AccessDeniedPath, templates);
    }

    [Fact]
    public void AccessDeniedPage_IsReachableByAKioskSession_WhileAdministrationStaysRefused()
    {
        Assert.False(KioskRestrictions.IsBlockedPath(new PathString(AuthenticationServices.AccessDeniedPath)));
        Assert.True(KioskRestrictions.IsBlockedPath(new PathString("/administration")));
    }

    private static ServiceProvider BuildAuthenticationServices()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development,
        });

        builder.ConfigureAuthenticationAndAuthorization(new BlocwerkSettings(new ConfigurationBuilder().Build()));

        return builder.Services.BuildServiceProvider();
    }
}
