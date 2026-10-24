// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.RegularExpressions;
using Blocwerk.Web.Components.OfflineClient;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline client's shell (<c>OfflineClientShell.razor</c>, Phase 3 PR 3.8). <c>@Assets[label]</c> hands back the
/// label itself for a label it does not know, and the page then requests a path that 404s without any build error;
/// these tests pin that every label the shell names has a fingerprinted endpoint in the app's static-asset manifest,
/// that the cache modules keep the Server pages' order, that the import map fingerprints the runtime, and that the
/// shell opens no circuit.
/// </summary>
public sealed partial class OfflineClientShellTests
{
    private static readonly string Root = FeedBulkSiteScanner.RepoRoot();

    // Markup only: the shell's comment names what it does not load.
    private static readonly string Shell = Comment().Replace(
        File.ReadAllText(Path.Combine(Root, "src", "Blocwerk.Web", "Components", "OfflineClient", "OfflineClientShell.razor")), string.Empty);

    [Fact]
    public void EveryLabel_TheShellNames_HasAFingerprintedEndpoint()
    {
        var fingerprinted = FingerprintedLabels();

        Assert.All(OfflineClientShellAssets.All, label => Assert.True(fingerprinted.Contains(label), $"{label} has no fingerprinted endpoint"));
        Assert.Contains(OfflineClientShellAssets.FrameworkPrefix + "dotnet.js", fingerprinted);
    }

    [Fact]
    public void TheCacheModules_LoadInTheServerPagesOrder()
    {
        var app = File.ReadAllText(Path.Combine(Root, "src", "Blocwerk.Web", "Components", "BlocwerkApp.razor"));
        var server = AppSharedScripts().Matches(app).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(server, OfflineClientShellAssets.CacheModules);
    }

    [Fact]
    public void TheClientStore_LoadsAfterTheCacheModules_AndBeforeTheRuntime()
    {
        var modules = Shell.IndexOf("OfflineClientShellAssets.CacheModules", StringComparison.Ordinal);
        var store = Shell.IndexOf("OfflineClientShellAssets.Store)", StringComparison.Ordinal);
        var loader = Shell.IndexOf("OfflineClientShellAssets.Loader)", StringComparison.Ordinal);

        Assert.True(modules >= 0 && modules < store && store < loader, "client-store.js reads the cache modules and is called by the runtime");
        Assert.Equal("v/js/client-store.js", OfflineClientShellAssets.Store);
    }

    [Fact]
    public void TheShell_HasTheClientBase_TheOfflinePartition_AndNoCircuit()
    {
        Assert.Contains("<base href=\"/v/\" />", Shell, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"bw-partition\" content=\"offline\" />", Shell, StringComparison.Ordinal);
        Assert.Contains("OfflineClientShellAssets.Loader)\" autostart=\"false\"", Shell, StringComparison.Ordinal);
        Assert.DoesNotContain("blazor.web.js", Shell, StringComparison.Ordinal);
        Assert.DoesNotContain("blazor.server.js", Shell, StringComparison.Ordinal);
        Assert.DoesNotContain("edit-presence", Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImportMap_MapsEachFingerprintedRuntimeModule_ByAbsolutePath()
    {
        var assets = new ResourceAssetCollection(
        [
            Asset("v/_framework/dotnet.ab12.js", "v/_framework/dotnet.js", "sha256-a"),
            Asset("v/_framework/dotnet.native.cd34.js", "v/_framework/dotnet.native.js", "sha256-b"),
            Asset("v/_framework/blazor.webassembly.js", null, null),
            Asset("v/_framework/Blocwerk.Client.ef56.wasm", "v/_framework/Blocwerk.Client.wasm", "sha256-c"),
            Asset("js/app.gh78.js", "js/app.js", "sha256-d"),
        ]);

        var map = OfflineClientShellAssets.ImportMap(assets);

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["/v/_framework/dotnet.js"] = "/v/_framework/dotnet.ab12.js",
                ["/v/_framework/dotnet.native.js"] = "/v/_framework/dotnet.native.cd34.js",
            },
            map.Imports);
        Assert.Equal("sha256-a", map.Integrity!["/v/_framework/dotnet.ab12.js"]);
    }

    [Fact]
    public void Url_IsRootRelative_SoTheBaseDoesNotMoveIt()
    {
        var assets = new ResourceAssetCollection([Asset("v/css/client.x1.css", "v/css/client.css", null)]);

        Assert.Equal("/v/css/client.x1.css", OfflineClientShellAssets.Url(assets, "v/css/client.css"));
    }

    private static ResourceAsset Asset(string url, string? label, string? integrity)
    {
        var properties = new List<ResourceAssetProperty>();
        if (label is not null)
        {
            properties.Add(new ResourceAssetProperty("label", label));
        }

        if (integrity is not null)
        {
            properties.Add(new ResourceAssetProperty("integrity", integrity));
        }

        return new ResourceAsset(url, properties);
    }

    // The app's build manifest beside the test assembly: each label that some endpoint serves under a fingerprint.
    private static HashSet<string> FingerprintedLabels()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Blocwerk.Web.staticwebassets.endpoints.json")));
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in manifest.RootElement.GetProperty("Endpoints").EnumerateArray())
        {
            var properties = endpoint.GetProperty("EndpointProperties").EnumerateArray()
                .ToDictionary(p => p.GetProperty("Name").GetString()!, p => p.GetProperty("Value").GetString());
            if (properties.TryGetValue("label", out var label) && properties.ContainsKey("fingerprint"))
            {
                labels.Add(label!);
            }
        }

        return labels;
    }

    [GeneratedRegex("@\\*.*?\\*@", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex("<script src=\"@Assets\\[\"(_content/Blocwerk\\.Shared\\.Ui/js/[^\"]+)\"\\]\"")]
    private static partial Regex AppSharedScripts();
}
