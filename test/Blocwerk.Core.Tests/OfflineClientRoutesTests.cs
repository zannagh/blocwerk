// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline client's route table (Phase 3 PR 3.8, routes from 3.11): the worker hands over exactly the declared
/// templates, each the @page of a client view and the Server's template of the same view; and the "Open it online" link
/// always leaves /v for a same-origin Server page.
/// </summary>
public sealed class OfflineClientRoutesTests
{
    // Client pages that are no gym route: the router's not-found page, and /v/ itself (the worker never hands "/" over).
    private static readonly string[] Undeclared = [string.Empty, ClientRoutes.NotOffline];

    [Fact]
    public void TheViews_DeclareTheWallList_TheWallView_AndTheBoulderDetail_WithTheirShareTwins()
    {
        Assert.Equal(
            ["walls", "walls/{WallId:guid}", "walls/{WallId:guid}/shared/{ShareToken}", "walls/{WallId:guid}/boulders/{BoulderId:guid}",
                "walls/{WallId:guid}/boulders/{BoulderId:guid}/shared/{ShareToken}"],
            ClientRoutes.Declared);
        Assert.Equal("/v/", ClientRoutes.BasePath);
    }

    [Fact]
    public void EveryDeclaredRoute_IsAClientPage_AndEveryClientPage_IsDeclared()
    {
        var pages = ClientPageTemplates();

        Assert.Equal(ClientRoutes.Declared.Order(StringComparer.Ordinal), pages.Except(Undeclared).Order(StringComparer.Ordinal));
        Assert.All(Undeclared, route => Assert.Contains(route, pages));
    }

    [Fact]
    public void EveryDeclaredRoute_IsTheServersTemplate_OfTheSameView()
    {
        var server = typeof(Blocwerk.Web.Components.BlocwerkApp).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>())
            .Select(r => r.Template.TrimStart('/'))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(ClientRoutes.Declared, route => Assert.Contains(route, server));
    }

    [Fact]
    public void TheViewsLinks_AreRelative_SoTheRouterStaysInTheClient_AndKeepTheShareToken()
    {
        var wall = Guid.NewGuid();
        var boulder = Guid.NewGuid();

        Assert.Equal($"walls/{wall}", ClientRoutes.WallLink(wall, null));
        Assert.Equal($"walls/{wall}/shared/t%2Fk", ClientRoutes.WallLink(wall, "t/k"));
        Assert.Equal($"walls/{wall}/boulders/{boulder}", ClientRoutes.BoulderLink(wall, boulder, string.Empty));
        Assert.Equal($"walls/{wall}/boulders/{boulder}/shared/tok", ClientRoutes.BoulderLink(wall, boulder, "tok"));
    }

    private static List<string> ClientPageTemplates() => typeof(ClientRoutes).Assembly.GetTypes()
        .SelectMany(t => t.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>())
        .Select(r => r.Template.TrimStart('/'))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    [Theory]
    [InlineData("", "/")]
    [InlineData("not-offline", "/")]
    [InlineData("Not-Offline?x=1", "/")]
    [InlineData("walls/3f2a", "/walls/3f2a")]
    [InlineData("walls/3f2a/boulders/9?view=list", "/walls/3f2a/boulders/9?view=list")]
    [InlineData("shared/tok#top", "/shared/tok#top")]
    public void ServerPath_DropsTheBase_AndKeepsTheQuery(string relative, string expected)
    {
        Assert.Equal(expected, ClientRoutes.ServerPath(relative));
    }

    [Theory]
    [InlineData("/evil.example/x")]
    [InlineData("//evil.example/x")]
    [InlineData("\\evil.example/x")]
    [InlineData("/\\evil.example/x")]
    public void ServerPath_NeverBuildsAProtocolRelativeUrl(string relative)
    {
        var path = ClientRoutes.ServerPath(relative);

        Assert.StartsWith("/", path, StringComparison.Ordinal);
        Assert.False(path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith("/\\", StringComparison.Ordinal), path);
    }

    [Theory]
    [InlineData("/v", "/v/")]
    [InlineData("/V", "/v/")]
    [InlineData("/V/", "/v/")]
    [InlineData("/V/walls/3f2a", "/v/walls/3f2a")]
    [InlineData("/v/Walls/3f2a", null)]
    [InlineData("/v/", null)]
    [InlineData("/v/walls", null)]
    [InlineData("/vx", null)]
    [InlineData("/walls/v", null)]
    public void Canonical_RedirectsOnlyTheBaseInAnotherCase(string path, string? expected)
    {
        Assert.Equal(expected, OfflineClientEndpoints.Canonical(path));
    }
}

