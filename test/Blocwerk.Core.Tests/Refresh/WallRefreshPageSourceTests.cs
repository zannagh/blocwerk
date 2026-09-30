// <copyright file="WallRefreshPageSourceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// Source assertions for the "Update panels + 3D" UI (no component-test host here): one admin-only entry per wall,
/// uploads over HTTP rather than the circuit, and an explicit confirm before anything goes live.
/// </summary>
public class WallRefreshPageSourceTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    [Fact]
    public void WallPage_OffersTheEntry_OnlyToAdminsOffTheKiosk()
    {
        var markup = Read("src/Blocwerk.Web/Components/Pages/Walls/WallDetail.razor");

        var entry = markup.IndexOf("href=\"/walls/@WallId/update\">Update panels + 3D</a>", StringComparison.Ordinal);
        var guard = markup.LastIndexOf("@if (_isAdmin && !KioskContext.IsKiosk)", entry, StringComparison.Ordinal);
        Assert.True(entry > 0, "entry link missing");
        Assert.True(guard > 0 && entry - guard < 200, "entry must sit right inside the admin/kiosk guard");
    }

    [Fact]
    public void Page_UploadsOverHttp_AndAppliesOnlyOnTheConfirmButton()
    {
        var page = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor");
        var drop = Read("src/Blocwerk.Web/Components/Shared/Refresh/RefreshDropZone.razor.cs");
        var confirm = Read("src/Blocwerk.Web/Components/Shared/Refresh/RefreshConfirm.razor");
        var app = Read("src/Blocwerk.Web/Components/BlocwerkApp.razor");

        Assert.Contains("@page \"/walls/{WallId:guid}/update\"", page);
        Assert.Contains("bwRefreshUpload.upload", drop);
        Assert.Contains("js/refresh-upload.js", app);
        Assert.Contains("@onclick=\"OnApply\">Apply update</button>", confirm);
        Assert.Contains("?update=review&amp;session=@View.UpdateSessionId\">Open full review</a>", confirm);
    }

    [Fact]
    public void FullReviewLink_OpensOnlyTheRunsOwnUpdate()
    {
        var markup = Read("src/Blocwerk.Web/Components/Pages/Walls/WallDetail.razor");

        Assert.Contains("[SupplyParameterFromQuery(Name = \"session\")]", markup);
        Assert.Contains("_openUpdate is null || _openUpdate.Id != UpdateSession", markup);
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory;
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Blocwerk.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
