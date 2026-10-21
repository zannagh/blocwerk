// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Reflection;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>[IgnoreAntiforgeryToken]</c> is safe only where no browser cookie can authorize the action: every write it covers
/// must pin the API-key scheme (a bearer header a cross-site form cannot send) and must not be anonymous. A new
/// controller or action that skips antiforgery while a cookie still reaches it fails here.
/// </summary>
public class IgnoreAntiforgeryApiKeyOnlyTests
{
    public static TheoryData<string> SkippingWrites()
    {
        var data = new TheoryData<string>();
        foreach (var (controller, action) in Writes().Where(w => Skips(w.Controller, w.Action)))
        {
            data.Add($"{controller.Name}.{action.Name}");
        }

        return data;
    }

    [Fact]
    public void TheOneReviewWrites_AreAmongTheCheckedActions()
    {
        var names = SkippingWrites().Select(row => (string)row[0]).ToList();

        Assert.Contains($"{nameof(WallRefreshApiController)}.{nameof(WallRefreshApiController.AnswerReview)}", names);
        Assert.Contains($"{nameof(WallRefreshApiController)}.{nameof(WallRefreshApiController.AcceptReviewGroup)}", names);
    }

    [Theory]
    [MemberData(nameof(SkippingWrites))]
    public void AWriteThatSkipsAntiforgery_IsAuthorizedByTheApiKeySchemeOnly(string name)
    {
        var (controller, action) = Writes().Single(w => $"{w.Controller.Name}.{w.Action.Name}" == name);
        var authorize = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(action.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .ToList();

        Assert.NotEmpty(authorize);
        Assert.All(authorize, a => Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, a.AuthenticationSchemes));
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));
        Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));
    }

    private static bool Skips(Type controller, MethodInfo action) =>
        controller.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>(inherit: true) is not null
        || action.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>(inherit: true) is not null;

    private static IEnumerable<(Type Controller, MethodInfo Action)> Writes() =>
        typeof(WallRefreshApiController).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes<HttpMethodAttribute>(inherit: true)
                    .Any(h => h.HttpMethods.Any(v => v is not ("GET" or "HEAD" or "OPTIONS"))))
                .Select(m => (t, m)));
}
