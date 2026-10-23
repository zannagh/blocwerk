// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client;
using Blocwerk.Web.Endpoints;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline client's route table (Phase 3 PR 3.8): it declares no gym route yet, so the service worker of PR 3.9 has
/// nothing to redirect to, and its "Open it online" link always leaves /v for a same-origin Server page.
/// </summary>
public sealed class OfflineClientRoutesTests
{
    [Fact]
    public void TheSkeleton_DeclaresNoRoute()
    {
        Assert.Empty(ClientRoutes.Declared);
        Assert.Equal("/v/", ClientRoutes.BasePath);
    }

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

