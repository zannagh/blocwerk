// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.RegularExpressions;
using Blocwerk.Authentication.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The device cache's page modules that live in <c>Blocwerk.Shared.Ui</c> (Phase 3 PR 3.7), so the offline client loads
/// the same files as the Server pages. <c>@Assets[label]</c> hands back the label itself for a label it does not know,
/// and the page then requests a path that 404s without any build error; these tests pin that every script label of
/// the root document and the signed-out page names a real file, and that the move kept the load order.
/// </summary>
public sealed partial class SharedUiScriptTests
{
    private const string SharedUi = "_content/Blocwerk.Shared.Ui/";

    private static readonly string[] Moved =
        ["asset-ledger-idb.js", "asset-ledger.js", "asset-wipe.js", "asset-lock.js", "asset-session.js", "asset-clear.js"];

    private static readonly string Root = FeedBulkSiteScanner.RepoRoot();

    [Fact]
    public void EveryScriptLabel_OfTheRootDocument_NamesAFileOnDisk()
    {
        // The framework's own script comes from the SDK, not from a project's wwwroot.
        var labels = AppScriptLabels().Where(l => !l.StartsWith("_framework/", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.True(File.Exists(FileOf(label)), $"{label} has no file at {FileOf(label)}"));
    }

    [Fact]
    public void EveryScriptLabel_OfTheSignedOutPage_NamesAFileOnDisk()
    {
        Assert.All(SignedOutMarkup.Scripts, label => Assert.True(File.Exists(FileOf(label)), $"{label} has no file at {FileOf(label)}"));
    }

    [Fact]
    public void TheMovedModules_LoadFromTheLibrary_AndNoCopyIsLeftInTheWebProject()
    {
        var labels = AppScriptLabels();
        foreach (var name in Moved)
        {
            Assert.Contains(SharedUi + "js/" + name, labels);
            Assert.DoesNotContain("js/" + name, labels);
            Assert.False(File.Exists(Path.Combine(Root, "src", "Blocwerk.Web", "wwwroot", "js", name)), $"a stale js/{name} is left in Blocwerk.Web");
        }
    }

    [Fact]
    public void TheLoadOrder_IsKept_OnBothPages()
    {
        // The ledger's IndexedDB half before the ledger, the ledger before the wipe rules, the lock after them, and the
        // session (which calls the lock and the wipe) after both; asset-clear.js near the end, before asset-report.js.
        string[] order = ["asset-ledger-idb.js", "asset-ledger.js", "asset-budget.js", "asset-wipe.js", "asset-lock.js", "asset-manifest.js",
            "asset-data.js", "asset-mine.js", "asset-store.js", "asset-session.js", "asset-plan.js", "asset-fill.js", "asset-cache.js",
            "asset-notice.js", "asset-3d.js", "asset-clear.js", "asset-report.js"];
        var app = AppScriptLabels().Select(Path.GetFileName).Where(n => n!.StartsWith("asset-", StringComparison.Ordinal)).ToList();
        Assert.Equal(order, app);

        var signedOut = SignedOutMarkup.Scripts.Select(Path.GetFileName).ToList();
        Assert.Equal(["deploy-drafts.js", "asset-ledger-idb.js", "asset-ledger.js", "asset-wipe.js", "asset-lock.js"], signedOut);
    }

    /// <summary>The service worker stays at the root path it is registered from, and it imports no moved module.</summary>
    [Fact]
    public void TheWorker_StaysWhereItIsRegistered_AndImportsNoMovedModule()
    {
        var web = Path.Combine(Root, "src", "Blocwerk.Web", "wwwroot", "js");
        var worker = File.ReadAllText(Path.Combine(web, "service-worker.js"));
        Assert.Contains("navigator.serviceWorker.register('/js/service-worker.js'", File.ReadAllText(Path.Combine(web, "pwa.js")));
        Assert.Contains("|| url.pathname.startsWith('/_content/')", worker);
        foreach (var imported in ImportScripts().Matches(worker).Select(m => m.Groups[1].Value))
        {
            Assert.True(File.Exists(Path.Combine(Root, "src", "Blocwerk.Web", "wwwroot", imported.TrimStart('/'))), imported);
            Assert.DoesNotContain(Path.GetFileName(imported), Moved);
        }
    }

    private static List<string> AppScriptLabels()
    {
        var app = File.ReadAllText(Path.Combine(Root, "src", "Blocwerk.Web", "Components", "BlocwerkApp.razor"));
        return AssetScript().Matches(app).Select(m => m.Groups[1].Value).ToList();
    }

    private static string FileOf(string label) => label.StartsWith(SharedUi, StringComparison.Ordinal)
        ? Path.Combine(Root, "src", "Blocwerk.Shared.Ui", "wwwroot", label[SharedUi.Length..])
        : Path.Combine(Root, "src", "Blocwerk.Web", "wwwroot", label);

    [GeneratedRegex("<script src=\"@Assets\\[\"([^\"]+)\"\\]\"")]
    private static partial Regex AssetScript();

    [GeneratedRegex("importScripts\\('([^']+)'\\)")]
    private static partial Regex ImportScripts();
}
