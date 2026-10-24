// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Client;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.OfflineClient;

/// <summary>
/// What the offline client's shell (<see cref="OfflineClientShell"/>) loads, by static-asset label, and the import map
/// that fingerprints the client's runtime modules. Every label resolves through the endpoint's
/// <see cref="ResourceAssetCollection"/> (<c>MapControllers().WithStaticAssets()</c>) to its fingerprinted URL, as
/// <c>@Assets</c> does in BlocwerkApp, so the shell names only immutable URLs.
/// </summary>
public static class OfflineClientShellAssets
{
    /// <summary>The client's own files sit under its static-asset base path.</summary>
    public const string ClientPrefix = "v/";

    /// <summary>The client's runtime modules, which <c>blazor.webassembly.js</c> imports by their plain names.</summary>
    public const string FrameworkPrefix = "v/_framework/";

    /// <summary>The stylesheets: the client's frame, then the shared overlay's.</summary>
    public static readonly string[] Styles = ["v/css/client.css", "_content/Blocwerk.Shared.Ui/css/hold-tap.css"];

    /// <summary>
    /// The device cache's shared modules (Blocwerk.Shared.Ui, PR 3.7), in the Server pages' order. Loading them only
    /// defines their globals; the client store (PR 3.10) starts them.
    /// </summary>
    public static readonly string[] CacheModules =
    [
        "_content/Blocwerk.Shared.Ui/js/asset-ledger-idb.js",
        "_content/Blocwerk.Shared.Ui/js/asset-ledger.js",
        "_content/Blocwerk.Shared.Ui/js/asset-wipe.js",
        "_content/Blocwerk.Shared.Ui/js/asset-lock.js",
        "_content/Blocwerk.Shared.Ui/js/asset-session.js",
        "_content/Blocwerk.Shared.Ui/js/asset-clear.js",
    ];

    /// <summary>
    /// The client store's reader of the device cache (PR 3.10: <c>window.bwClientStoreRows</c>, then
    /// <c>window.bwClientStore</c>), in order: loaded after the cache modules they read through, before the runtime that
    /// calls them.
    /// </summary>
    public static readonly string[] StoreModules = ["v/js/client-store-rows.js", "v/js/client-store.js"];

    /// <summary>
    /// The sync loop's network side (PR 3.12): Web's docs-index and marks refreshers (with asset-manifest.js, whose
    /// <c>signedOut</c> they call), so the client writes the ledger exactly as the Server pages do, then
    /// <c>window.bwClientSync</c>. Loading them only defines their globals.
    /// </summary>
    public static readonly string[] SyncModules = ["js/asset-manifest.js", "js/asset-data.js", "js/asset-mine.js", "v/js/client-sync.js"];

    /// <summary>The Blazor WebAssembly loader, started by <see cref="Boot"/> (autostart off).</summary>
    public const string Loader = "v/_framework/blazor.webassembly.js";

    /// <summary>Starts the loader and shows the boot-failure UI when the start fails or never renders.</summary>
    public const string Boot = "v/js/client-boot.js";

    /// <summary>
    /// The worker-loss guard (Web's <c>js/sw-guard.js</c>, PR 3.9), which the Server pages load too: a page whose
    /// controller no longer answers reloads once the server is reachable.
    /// </summary>
    public const string Guard = "js/sw-guard.js";

    private static readonly ConditionalWeakTable<ResourceAssetCollection, string> Versions = new();

    /// <summary>Every label the shell names, for the tests that pin each one to a served file.</summary>
    public static IEnumerable<string> All => Styles.Concat(CacheModules).Concat(StoreModules).Concat(SyncModules).Append(Guard).Append(Loader).Append(Boot);

    /// <summary>
    /// The client's declared routes as the shell's <c>bw-client-routes</c> meta carries them: space-separated
    /// templates relative to /v/. The worker hands a failed navigation over only for a path one of them covers.
    /// </summary>
    public static string Routes => string.Join(' ', ClientRoutes.Declared);

    /// <summary>
    /// The client version this deploy serves (PR 3.9): a hash of every URL the shell names, the import map's modules
    /// and the declared routes. The URLs are fingerprinted, so any change to a file the shell loads, to dotnet.js's
    /// boot manifest (it names the assemblies) or to the routes is a new version. The Server pages and the shell carry
    /// it in a <c>bw-client-version</c> meta; the worker precaches one version at a time under it.
    /// </summary>
    /// <param name="assets">The endpoint's asset collection.</param>
    /// <returns>16 lowercase hex digits.</returns>
    public static string Version(ResourceAssetCollection assets) => Versions.GetValue(assets, ComputeVersion);

    /// <summary>A label's served URL, root-relative: the shell's base is /v/, so a relative URL would resolve under it.</summary>
    /// <param name="assets">The endpoint's asset collection.</param>
    /// <param name="label">The static-asset label.</param>
    /// <returns>The fingerprinted URL when the collection knows the label, else the plain path.</returns>
    public static string Url(ResourceAssetCollection assets, string label) => "/" + assets[label].TrimStart('/');

    /// <summary>
    /// The import map for the client's runtime: every fingerprinted <c>v/_framework/*.js</c> under its plain name, with
    /// its integrity. The keys are absolute because the shell's base (/v/) is what an import map key resolves against.
    /// <c>blazor.webassembly.js</c> imports <c>./dotnet.js</c>, which this maps to the fingerprinted file.
    /// </summary>
    /// <param name="assets">The endpoint's asset collection.</param>
    /// <returns>The definition the shell's <c>ImportMap</c> renders.</returns>
    public static ImportMapDefinition ImportMap(ResourceAssetCollection assets)
    {
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        var integrity = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            var label = Property(asset, "label");
            if (label is null || asset.Url == label || !label.StartsWith(FrameworkPrefix, StringComparison.Ordinal)
                || !label.EndsWith(".js", StringComparison.Ordinal))
            {
                continue;
            }

            imports["/" + label] = "/" + asset.Url;
            if (Property(asset, "integrity") is { } hash)
            {
                integrity["/" + asset.Url] = hash;
            }
        }

        return new ImportMapDefinition(imports, null, integrity);
    }

    private static string ComputeVersion(ResourceAssetCollection assets)
    {
        var imports = ImportMap(assets).Imports ?? new Dictionary<string, string>();
        var named = All.Select(label => Url(assets, label))
            .Concat(imports.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key + "=" + i.Value))
            .Append("routes:" + Routes);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', named)));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static string? Property(ResourceAsset asset, string name) =>
        asset.Properties?.FirstOrDefault(p => p.Name == name)?.Value;
}
