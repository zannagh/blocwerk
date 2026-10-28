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
    public void Version_Changes_WhenA3DModuleChanges_ThoughTheShellNeverLoadsItAtBoot()
    {
        static ResourceAssetCollection With(string spark) => new(
        [
            Asset("v/_framework/dotnet.ab12.js", "v/_framework/dotnet.js"),
            Asset("lib/spark/" + spark, "lib/spark/spark.module.min.js"),
        ]);

        Assert.NotEqual(OfflineClientShellAssets.Version(With("spark.module.min.aaaaaaaaaa.js")), OfflineClientShellAssets.Version(With("spark.module.min.bbbbbbbbbb.js")));
    }

    [Fact]
    public void Routes_AreTheDeclaredTemplates_SpaceSeparated()
    {
        Assert.Equal(string.Join(' ', ClientRoutes.Declared), OfflineClientShellAssets.Routes);
        Assert.All(ClientRoutes.Declared, route => Assert.DoesNotContain(' ', route));
    }

    [Fact]
    public void EveryDeclaredRoute_StartsWithALiteralSegment_NeverACatchAllOrARootParameter()
    {
        Assert.All(ClientRoutes.Declared, route => Assert.True(StartsWithLiteral(route), $"{route} must start with a literal segment"));

        // The rule itself: the worker hands over whatever a template covers, so a leading parameter or catch-all would
        // cover every Server path (its deny list only names the auth, kiosk, dev and admin roots).
        Assert.True(StartsWithLiteral("walls/{w}/boulders/{b}"));
        Assert.True(StartsWithLiteral("shared/{t}/{*rest}"));
        Assert.False(StartsWithLiteral("{w}"));
        Assert.False(StartsWithLiteral("{*rest}"));
        Assert.False(StartsWithLiteral("{**rest}"));
        Assert.False(StartsWithLiteral(string.Empty));
        Assert.False(StartsWithLiteral("v/walls"));
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

    // A first segment that is a literal (no braces) and not the client's own base.
    private static bool StartsWithLiteral(string template)
    {
        var first = template.TrimStart('/').Split('/')[0];
        return first.Length > 0 && !first.Contains('{', StringComparison.Ordinal) && !first.Equals("v", StringComparison.OrdinalIgnoreCase);
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
