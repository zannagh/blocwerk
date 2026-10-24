// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;
using Blocwerk.Web.Components.OfflineClient;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// Everything a view loads is named by the shell (plan §4, the 3.11/3.13 note): the worker precaches only the files the
/// rendered shell names (plus dotnet.js's boot resources), and the client version hashes the same set, so anything else
/// fails offline and ships no new version. These pin each way a view could load a file the shell does not name: a
/// stylesheet's <c>url()</c>, a client file the shell does not load, a lazily imported module or script, a component's
/// own <c>&lt;script&gt;</c>, <c>&lt;link&gt;</c> or static image, a JS global no named module defines (Web's
/// <c>bwAssetCache</c> and the like), and a lazy or satellite assembly.
/// </summary>
public sealed partial class OfflineClientViewAssetsTests
{
    private static readonly string Root = FeedBulkSiteScanner.RepoRoot();
    private static readonly string ClientDir = Path.Combine(Root, "src", "Blocwerk.Client");
    private static readonly HashSet<string> Named = OfflineClientShellAssets.All.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryUrlANamedStylesheetLoads_IsNamedByTheShell()
    {
        foreach (var label in OfflineClientShellAssets.Styles)
        {
            Assert.Empty(UnnamedUrls(label, File.ReadAllText(Source(label))));
        }
    }

    [Fact]
    public void TheStylesheetCheck_CatchesAnUnnamedIcon_AndLetsANamedOrDataUrlThrough()
    {
        const string css = ".a { background: url(\"../icons/star.svg\"); } .b { background: url(data:image/png;base64,AA==); } .c { background: url('client.css'); }";

        Assert.Equal(["v/icons/star.svg"], UnnamedUrls("v/css/client-views.css", css));
    }

    [Fact]
    public void EveryClientScriptAndStylesheet_IsNamedByTheShell()
    {
        var files = Directory.EnumerateFiles(Path.Combine(ClientDir, "wwwroot"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".js", StringComparison.Ordinal) || f.EndsWith(".css", StringComparison.Ordinal))
            .Select(f => "v/" + Path.GetRelativePath(Path.Combine(ClientDir, "wwwroot"), f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("v/css/client-views.css", files);
        Assert.Contains("v/js/client-view.js", files);
        Assert.All(files, label => Assert.Contains(label, Named));
    }

    [Fact]
    public void NoNamedScript_LoadsAnotherFile()
    {
        foreach (var label in Named.Where(l => l.EndsWith(".js", StringComparison.Ordinal) && !l.StartsWith(OfflineClientShellAssets.FrameworkPrefix, StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(DynamicLoad(), File.ReadAllText(Source(label)));
        }
    }

    [Fact]
    public void TheClientsComponents_LoadNoFileOfTheirOwn()
    {
        foreach (var file in ClientSources())
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotMatch("<script|<link|IJSObjectReference|\"import\"|url\\(", text);
            foreach (Match src in StaticSrc().Matches(text))
            {
                Assert.Fail($"{Path.GetFileName(file)} loads {src.Value}, which the shell does not name");
            }
        }
    }

    [Fact]
    public void EveryJsGlobalTheClientCalls_IsDefinedByANamedModule_AndNoneIsWebOnly()
    {
        var called = ClientSources().SelectMany(f => JsGlobal().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var named = Named.Where(l => l.EndsWith(".js", StringComparison.Ordinal) && !l.StartsWith(OfflineClientShellAssets.FrameworkPrefix, StringComparison.Ordinal))
            .Select(l => File.ReadAllText(Source(l)))
            .ToList();

        Assert.Contains("bwClientView", called);
        Assert.Contains("bwClientStore", called);
        foreach (var global in called)
        {
            Assert.True(named.Any(js => js.Contains($"window.{global} =", StringComparison.Ordinal)), $"{global} is defined by no module the shell names");
        }

        Assert.Empty(called.Intersect(["bwAssetStore", "bwAssetCache", "bwAssetReport", "bwDeployDrafts", "bwKiosk"]));
    }

    [Fact]
    public void TheClient_HasNoLazyOrSatelliteAssembly()
    {
        var csproj = File.ReadAllText(Path.Combine(ClientDir, "Blocwerk.Client.csproj"));

        Assert.DoesNotContain("BlazorWebAssemblyLazyLoad", csproj, StringComparison.Ordinal);
        Assert.Contains("<InvariantGlobalization>true</InvariantGlobalization>", csproj, StringComparison.Ordinal);
        foreach (var project in new[] { "Blocwerk.Client", "Blocwerk.Shared.Ui", "Blocwerk.Contracts" })
        {
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.resx", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
        }
    }

    // A url() of a stylesheet, resolved against the stylesheet's label, that is neither data: nor a label the shell names.
    private static List<string> UnnamedUrls(string label, string css)
    {
        var dir = label[..(label.LastIndexOf('/') + 1)];
        return CssUrl().Matches(css)
            .Select(m => m.Groups[1].Value.Trim().Trim('"', '\''))
            .Where(u => !u.StartsWith("data:", StringComparison.Ordinal))
            .Select(u => u.StartsWith('/') ? u.TrimStart('/') : new Uri(new Uri("http://x/" + dir), u).AbsolutePath.TrimStart('/'))
            .Where(u => !Named.Contains(u))
            .ToList();
    }

    // A shell label's source file: the client's, the shared library's, or Web's own.
    private static string Source(string label) => label switch
    {
        _ when label.StartsWith("v/", StringComparison.Ordinal) => Path.Combine(ClientDir, "wwwroot", label[2..]),
        _ when label.StartsWith("_content/Blocwerk.Shared.Ui/", StringComparison.Ordinal) =>
            Path.Combine(Root, "src", "Blocwerk.Shared.Ui", "wwwroot", label["_content/Blocwerk.Shared.Ui/".Length..]),
        _ => Path.Combine(Root, "src", "Blocwerk.Web", "wwwroot", label),
    };

    private static IEnumerable<string> ClientSources() => Directory.EnumerateFiles(ClientDir, "*.*", SearchOption.AllDirectories)
        .Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [GeneratedRegex("url\\(([^)]*)\\)")]
    private static partial Regex CssUrl();

    [GeneratedRegex("import\\(|importScripts|createElement\\(['\"](script|link|img)|new Worker|\\.src\\s*=")]
    private static partial Regex DynamicLoad();

    // A src or href attribute with a literal file path (not a Razor expression, not a client route or a Server page).
    [GeneratedRegex("(src|href)=\"(?!@)[^\"]*\\.(js|css|png|svg|jpg|jpeg|webp|woff2?|wasm|json)\"")]
    private static partial Regex StaticSrc();

    // A JS identifier the client calls: "bwSomething.member".
    [GeneratedRegex("\"(bw[A-Z][A-Za-z]*)\\.[A-Za-z]+\"")]
    private static partial Regex JsGlobal();
}
