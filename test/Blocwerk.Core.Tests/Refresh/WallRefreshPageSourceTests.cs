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
        var guard = markup.LastIndexOf("@if (!KioskContext.IsKiosk && string.IsNullOrEmpty(ShareToken))", entry, StringComparison.Ordinal);
        var settings = markup.IndexOf("<h2>Wall Settings</h2>", StringComparison.Ordinal);
        var adminOnly = markup.LastIndexOf("@if (_isAdmin)", settings, StringComparison.Ordinal);
        Assert.True(entry > 0, "entry link missing");
        Assert.True(guard > 0 && entry - guard < 200, "entry must sit right inside the kiosk/share-link guard");
        Assert.True(adminOnly > 0 && settings > adminOnly && entry > settings, "entry must live in the admin-only Wall Settings card");
        Assert.Equal(entry, markup.LastIndexOf("href=\"/walls/@WallId/update\"", StringComparison.Ordinal));
        Assert.Contains("\"Update wall with new photos\"", markup);
    }

    [Fact]
    public void ARefusedAction_ReloadsTheRun_SoAStaleSummaryIsReplaced()
    {
        var code = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor.cs");

        var act = code.IndexOf("private async Task ActAsync(", StringComparison.Ordinal);
        var reload = code.IndexOf("await ReloadAsync();", act, StringComparison.Ordinal);
        var finallyAt = code.IndexOf("finally", act, StringComparison.Ordinal);
        Assert.True(act > 0 && finallyAt > act && reload > finallyAt, "ActAsync must reload in a finally block");
        Assert.Contains("Refreshes.ApplyAsync(id, confirmed)", code);
    }

    [Fact]
    public void Discard_AsksFirst_InThePage()
    {
        var page = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor");

        Assert.Contains("@onclick=\"AskDiscard\" disabled=\"@busy\">Discard this update</button>", page);
        Assert.Contains("@ref=\"discardButton\" @onclick=\"DiscardAsync\" disabled=\"@busy\">Discard</button>", page);
        Assert.Contains("@onclick=\"() => confirmDiscard = false\" disabled=\"@busy\">Keep</button>", page);
        Assert.DoesNotContain("confirm(", page);
    }

    [Fact]
    public void DiscardConfirm_IsScrolledClearOfTheBottomBanners()
    {
        var code = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor.cs");
        var css = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor.css");
        var js = Read("src/Blocwerk.Web/wwwroot/js/reveal-section.js");

        Assert.Contains("InvokeVoidAsync(\"revealElement\", discardButton)", code);
        Assert.Contains("export function revealElement(element)", js);
        Assert.Contains("scroll-margin-bottom: calc(24px + var(--bottomnav-h, 0px))", css);
    }

    [Fact]
    public void SortScreen_NamesPanelsByPlace_AndSaysWhatToCheck()
    {
        var sort = Read("src/Blocwerk.Web/Components/Shared/Refresh/RefreshSortScreen.razor.cs");

        Assert.Contains("PanelPositionName.Describe(pick.Col, pick.Row)", sort);
        Assert.Contains("\"Check this match\"", sort);
        Assert.DoesNotContain("?\"", sort);
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
        Assert.Contains("@onclick=\"OnApply\" disabled=\"@(View.Check3DPending || View.SummaryUpdating)\">Apply update</button>", confirm);
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
