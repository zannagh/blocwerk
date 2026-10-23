// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The offline client's static shell (<c>Blocwerk.Client/wwwroot/index.html</c>, Phase 3 PR 3.8). It is plain HTML, so
/// nothing at build time checks the files it names: these tests pin that every shared-library file it loads exists
/// (a wrong name 404s silently), in the order the Server page loads them, and that it opens no circuit.
/// </summary>
public sealed partial class OfflineClientShellTests
{
    private static readonly string Root = FeedBulkSiteScanner.RepoRoot();

    // Markup only: the shell's comments name what it does not load.
    private static readonly string Shell =
        Comment().Replace(File.ReadAllText(Path.Combine(Root, "src", "Blocwerk.Client", "wwwroot", "index.html")), string.Empty);

    [Fact]
    public void TheShell_HasTheClientBase_AndTheOfflinePartition()
    {
        Assert.Contains("<base href=\"/v/\" />", Shell, StringComparison.Ordinal);
        Assert.Contains("<meta name=\"bw-partition\" content=\"offline\" />", Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShell_LoadsNoCircuit_AndNoEditPresence()
    {
        Assert.DoesNotContain("blazor.web.js", Shell, StringComparison.Ordinal);
        Assert.DoesNotContain("blazor.server.js", Shell, StringComparison.Ordinal);
        Assert.DoesNotContain("edit-presence", Shell, StringComparison.Ordinal);
        Assert.Contains("<script src=\"_framework/blazor.webassembly.js\"></script>", Shell, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySharedLibraryFile_TheShellNames_IsOnDisk()
    {
        var files = SharedRefs().Matches(Shell).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(7, files.Count);
        Assert.All(files, f => Assert.True(
            File.Exists(Path.Combine(Root, "src", "Blocwerk.Shared.Ui", "wwwroot", f)), $"{f} is not in Blocwerk.Shared.Ui/wwwroot"));
    }

    [Fact]
    public void TheCacheModules_LoadInTheServerPagesOrder()
    {
        var shell = SharedRefs().Matches(Shell).Select(m => m.Groups[1].Value).Where(f => f.StartsWith("js/", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        var app = File.ReadAllText(Path.Combine(Root, "src", "Blocwerk.Web", "Components", "BlocwerkApp.razor"));
        var server = AppSharedScripts().Matches(app).Select(m => Path.GetFileName(m.Groups[1].Value)).ToList();

        Assert.Equal(server, shell);
    }

    [Fact]
    public void TheClientsOwnFiles_TheShellNames_AreOnDisk()
    {
        var css = OwnCss().Matches(Shell).Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(css);
        Assert.All(css, f => Assert.True(File.Exists(Path.Combine(Root, "src", "Blocwerk.Client", "wwwroot", f)), f));
    }

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comment();

    // The library sits at /_content/, outside the /v/ base, so the shell climbs out of it.
    [GeneratedRegex("(?:href|src)=\"\\.\\./_content/Blocwerk\\.Shared\\.Ui/([^\"]+)\"")]
    private static partial Regex SharedRefs();

    [GeneratedRegex("<script src=\"@Assets\\[\"_content/Blocwerk\\.Shared\\.Ui/(js/[^\"]+)\"\\]\"")]
    private static partial Regex AppSharedScripts();

    [GeneratedRegex("<link rel=\"stylesheet\" href=\"(css/[^\"]+)\"")]
    private static partial Regex OwnCss();
}
