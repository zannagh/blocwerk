// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture package routes are machine routes for the rollout script: under the API-key surface, the scheme pinned to
/// the API key (no browser cookie reaches them), a personal key with write access only; the app-admin check is the
/// service's (see <see cref="CapturePackageExportTests"/>, <see cref="CapturePackageImportTests"/>).
/// </summary>
public class CapturePackageApiAuthorizationTests
{
    [Theory]
    [InlineData(typeof(CapturePackagesController))]
    [InlineData(typeof(CaptureImportsController))]
    public void TheRoutes_ArePersonalWriteKeyOnly_UnderTheApiKeySurface(Type controller)
    {
        var authorize = Assert.Single(controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        var route = Assert.Single(controller.GetCustomAttributes<RouteAttribute>(inherit: true));

        Assert.Equal(BlocwerkPolicies.HumanOrUserApiKey, authorize.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.StartsWith("api/v1/admin/", route.Template, StringComparison.Ordinal);
        Assert.True(ApiKeySurface.Covers(new PathString("/" + route.Template)));
    }
}
