// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client;
using Blocwerk.Web.Components.OfflineClient;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline client's version and routes metas (Phase 3 PR 3.9): the service worker precaches one version at a time
/// under <see cref="OfflineClientShellAssets.Version"/> and hands a failed navigation over only for a route the shell's
/// <c>bw-client-routes</c> names, so the version must change with any file the shell loads and the Server pages must
/// carry the same value.
/// </summary>
public sealed class OfflineClientVersionTests
{
    private static readonly string Root = FeedBulkSiteScanner.RepoRoot();

    [Fact]
    public void Version_IsSixteenHexDigits_AndTheSameForTheSameAssets()
    {
        var version = OfflineClientShellAssets.Version(Assets("dotnet.ab12.js"));

        Assert.Matches("^[0-9a-f]{16}$", version);
        Assert.Equal(version, OfflineClientShellAssets.Version(Assets("dotnet.ab12.js")));
    }

    [Fact]
    public void Version_Changes_WhenAFileTheShellLoadsChanges()
    {
        var before = OfflineClientShellAssets.Version(Assets("dotnet.ab12.js"));

        Assert.NotEqual(before, OfflineClientShellAssets.Version(Assets("dotnet.cd34.js")));
        Assert.NotEqual(before, OfflineClientShellAssets.Version(Assets("dotnet.ab12.js", boot: "client-boot.ff00.js")));
    }

    [Fact]
    public void Routes_AreTheDeclaredTemplates_SpaceSeparated()
    {
        Assert.Equal(string.Join(' ', ClientRoutes.Declared), OfflineClientShellAssets.Routes);
        Assert.All(ClientRoutes.Declared, route => Assert.DoesNotContain(' ', route));
    }

    [Fact]
    public void TheShellAndTheServerPages_CarryTheVersion_AndTheShellTheRoutesAndTheGuard()
    {
        var shell = Read("OfflineClient", "OfflineClientShell.razor");
        var app = Read("BlocwerkApp.razor");
        const string meta = "<meta name=\"bw-client-version\" content=\"@OfflineClientShellAssets.Version(Assets)\" />";

        Assert.Contains(meta, shell, StringComparison.Ordinal);
        Assert.Contains(meta, app, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"bw-client-routes\" content=\"@OfflineClientShellAssets.Routes\" />", shell, StringComparison.Ordinal);
        Assert.Contains("OfflineClientShellAssets.Guard)\"></script>", shell, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"js/sw-guard.js\"]", app, StringComparison.Ordinal);
        Assert.Contains("@Assets[\"js/client-precache.js\"]", app, StringComparison.Ordinal);
        Assert.True(app.IndexOf("js/asset-cache.js", StringComparison.Ordinal) < app.IndexOf("js/client-precache.js", StringComparison.Ordinal));
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, "src", "Blocwerk.Web", "Components", .. parts]));

    private static ResourceAssetCollection Assets(string dotnet, string boot = "client-boot.ee11.js") => new(
    [
        Asset("v/_framework/" + dotnet, "v/_framework/dotnet.js"),
        Asset("v/js/" + boot, OfflineClientShellAssets.Boot),
        Asset("v/css/client.aa11.css", "v/css/client.css"),
    ]);

    private static ResourceAsset Asset(string url, string label) =>
        new(url, [new ResourceAssetProperty("label", label)]);
}
