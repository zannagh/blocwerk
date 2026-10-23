// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

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

    /// <summary>The Blazor WebAssembly loader, started by <see cref="Boot"/> (autostart off).</summary>
    public const string Loader = "v/_framework/blazor.webassembly.js";

    /// <summary>Starts the loader and shows the boot-failure UI when the start fails or never renders.</summary>
    public const string Boot = "v/js/client-boot.js";

    /// <summary>Every label the shell names, for the tests that pin each one to a served file.</summary>
    public static IEnumerable<string> All => Styles.Concat(CacheModules).Append(Loader).Append(Boot);

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

    private static string? Property(ResourceAsset asset, string name) =>
        asset.Properties?.FirstOrDefault(p => p.Name == name)?.Value;
}
