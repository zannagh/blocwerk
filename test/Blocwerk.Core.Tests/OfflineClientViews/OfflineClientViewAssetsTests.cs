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
    public void NoNamedScript_LoadsAnotherFile_ButAModuleTheShellNames()
    {
        var scripts = Named.Where(l => l.EndsWith(".js", StringComparison.Ordinal) && !l.StartsWith(OfflineClientShellAssets.FrameworkPrefix, StringComparison.Ordinal))
            .Except(OfflineClient3DAssets.Modules);
        foreach (var label in scripts)
        {
            var text = File.ReadAllText(Source(label));
            foreach (Match import in LiteralImport().Matches(text))
            {
                Assert.Contains(import.Groups[1].Value.TrimStart('/'), Named);
            }

            Assert.DoesNotMatch(DynamicLoad(), LiteralImport().Replace(text, string.Empty));
        }

        Assert.Equal(
            ["js/wall3d.js", "js/wall3d-splat-hold.js"],
            LiteralImport().Matches(File.ReadAllText(Source("v/js/client-3d-stage.js"))).Select(m => m.Groups[1].Value.TrimStart('/')));
    }

    [Fact]
    public void The3DModules_AreWall3dsWholeImportGraph()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>(["js/wall3d.js", "js/wall3d-splat-hold.js"]);
        while (todo.TryPop(out var label))
        {
            if (!seen.Add(label))
            {
                continue;
            }

            var code = Comments().Replace(File.ReadAllText(Source(label)), string.Empty);
            foreach (Match import in ModuleImport().Matches(code))
            {
                var spec = import.Groups.Cast<Group>().Skip(1).First(g => g.Success).Value;
                Assert.StartsWith(".", spec, StringComparison.Ordinal);
                todo.Push(new Uri(new Uri("http://x/" + label), spec).AbsolutePath.TrimStart('/'));
            }
        }

        Assert.Equal(seen.Order(StringComparer.Ordinal), OfflineClient3DAssets.Modules.Order(StringComparer.Ordinal));
        Assert.All(OfflineClient3DAssets.Modules, label => Assert.Contains(label, Named));
        Assert.Contains(OfflineClient3DAssets.Stylesheet, OfflineClientShellAssets.Styles);
    }

    [Fact]
    public void The3DModules_LoadNoFileOfTheirOwn_ButWhatTheShellNames()
    {
        var loads = OfflineClient3DAssets.Modules
            .SelectMany(label => DynamicLoad().Matches(File.ReadAllText(Source(label))).Select(m => $"{label}: {m.Value}"))
            .ToList();

        // Spark on demand (named: the graph test), wall3d.css (named: a shell stylesheet), the view's own texture URLs (the
        // asset cache's, three.js's ImageLoader), and Spark's decode worker from its inline source (a blob, no file).
        Assert.Equal(
            [
                "js/wall3d-splat-levels.js: import(", "js/wall3d-stage.js: createElement('link", "lib/three/three.core.min.js: .src=",
                "lib/spark/spark.module.min.js: new Worker", "lib/spark/spark.module.min.js: new Worker", "lib/spark/spark.module.min.js: .src =",
            ],
            loads);
        Assert.Contains("new URL('../css/wall3d.css', import.meta.url)", File.ReadAllText(Source("js/wall3d-stage.js")), StringComparison.Ordinal);
        Assert.Contains("createObjectURL(DB)", File.ReadAllText(Source("lib/spark/spark.module.min.js")), StringComparison.Ordinal);
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

    // A dynamic import of a literal URL: allowed when the shell names that module (its import map).
    [GeneratedRegex("import\\(\\s*'([^']+)'\\s*\\)")]
    private static partial Regex LiteralImport();

    // A module's static import or export-from, and a literal dynamic import (three.js's minified `from"./x.js"` too).
    [GeneratedRegex("(?:import|export)\\s[^'\";]*?from\\s*['\"]([^'\"]+)['\"]|import\\(\\s*['\"]([^'\"]+)['\"]\\s*\\)|from\"([^\"]+)\"")]
    private static partial Regex ModuleImport();

    // Block and line comments (a line comment only at a line's start, so URLs inside code stay).
    [GeneratedRegex("/\\*.*?\\*/|(?m)^[ \\t]*//[^\\n]*", RegexOptions.Singleline)]
    private static partial Regex Comments();

    // A src or href attribute with a literal file path (not a Razor expression, not a client route or a Server page).
    [GeneratedRegex("(src|href)=\"(?!@)[^\"]*\\.(js|css|png|svg|jpg|jpeg|webp|woff2?|wasm|json)\"")]
    private static partial Regex StaticSrc();

    // A JS identifier the client calls: "bwSomething.member".
    [GeneratedRegex("\"(bw[A-Z][A-Za-z]*)\\.[A-Za-z]+\"")]
    private static partial Regex JsGlobal();
}
