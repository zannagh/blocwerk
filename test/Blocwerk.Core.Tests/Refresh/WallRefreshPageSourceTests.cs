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

        // Two links to the one update page: the settings page's "Update wall" and the Wall Settings card's; both admin-only,
        // both inside the kiosk/share-link guard.
        var links = AllIndexesOf(markup, "href=\"/walls/@WallId/update\"");
        Assert.Equal(2, links.Count);
        foreach (var link in links)
        {
            var guard = markup.LastIndexOf("!KioskContext.IsKiosk && string.IsNullOrEmpty(ShareToken)", link, StringComparison.Ordinal);
            Assert.True(guard > 0 && link - guard < 200, "each entry must sit right inside the kiosk/share-link guard");
            Assert.True(markup.LastIndexOf("_isAdmin", link, StringComparison.Ordinal) > 0, "each entry is for wall admins");
        }

        var rail = markup.IndexOf("class=\"btn-secondary wall-update-link\" href=\"/walls/@WallId/update\">Update wall</a>", StringComparison.Ordinal);
        Assert.True(rail > 0, "the wall page offers \"Update wall\"");

        // On the settings page, right below the Activity card, not on the overview under "View in 3D" (a stray tap there
        // started an update).
        var activity = markup.IndexOf("<h2>Activity</h2>", StringComparison.Ordinal);
        var activityEnd = markup.IndexOf("</details>", activity, StringComparison.Ordinal);
        var wallSettings = markup.IndexOf("<LazySettingsCard Title=\"Wall Settings\"", StringComparison.Ordinal);
        Assert.True(activityEnd > 0 && rail > activityEnd && rail < wallSettings, "\"Update wall\" sits between the Activity and Wall Settings cards");
        Assert.True(rail < markup.IndexOf("<Wall3DLink", StringComparison.Ordinal), "\"Update wall\" is not on the overview");

        // The manual wizard is an advanced route, and deleting the wall is far from every update control.
        var advanced = markup.IndexOf("<summary>Advanced: step by step</summary>", StringComparison.Ordinal);
        Assert.True(advanced > 0 && markup.IndexOf("\"Update step by step\"", advanced, StringComparison.Ordinal) > advanced);
        var delete = markup.IndexOf("Label=\"Delete Wall\"", StringComparison.Ordinal);
        var deleteCard = markup.IndexOf("<LazySettingsCard Title=\"Delete this wall\"", StringComparison.Ordinal);
        var lastCard = markup.IndexOf("<LazySettingsCard Title=\"Kiosk Registration\"", StringComparison.Ordinal);
        Assert.True(deleteCard > lastCard && delete > deleteCard, "Delete Wall lives in its own card after every other settings card");
    }

    [Fact]
    public void TheReviewsWriter_SurvivesAnyFailure_KeepsUnsavedAnswers_AndGuardsTheTab()
    {
        var writes = Read("src/Blocwerk.Web/Components/Shared/Refresh/Review/UpdateReviewScreen.Writes.cs");
        var screen = Read("src/Blocwerk.Web/Components/Shared/Refresh/Review/UpdateReviewScreen.razor");

        // The loop clears its flag in a finally, whatever a batch throws, and a failed batch is kept for Retry.
        var loop = writes.IndexOf("private async Task WriteLoopAsync()", StringComparison.Ordinal);
        var finallyAt = writes.IndexOf("finally", loop, StringComparison.Ordinal);
        Assert.True(finallyAt > loop && writes.IndexOf("sending = false;", finallyAt, StringComparison.Ordinal) > finallyAt);
        Assert.Contains("catch (Exception ex) when (ex is not OperationCanceledException", writes);
        Assert.Contains("pending.Failed(batch);", writes);

        // Leaving the review takes the "leave this page?" guard down (it is about this page's answers only).
        var code = Read("src/Blocwerk.Web/Components/Shared/Refresh/Review/UpdateReviewScreen.razor.cs");
        var dispose = code.IndexOf("public async ValueTask DisposeAsync()", StringComparison.Ordinal);
        Assert.True(dispose > 0 && code.IndexOf("await GuardTabAsync(false);", dispose, StringComparison.Ordinal) > dispose);
        Assert.Contains("export function guard(enabled)", Read("src/Blocwerk.Web/wwwroot/js/unsaved-guard.js"));
        Assert.Contains("removeEventListener('beforeunload', ask)", Read("src/Blocwerk.Web/wwwroot/js/unsaved-guard.js"));
        Assert.Contains("@onclick=\"RetryAsync\"", screen);

        // An answer reads "saving…" until the server has it; closing the tab meanwhile asks first.
        Assert.Contains("AnsweredBy = answer is null ? null : Saving", Read("src/Blocwerk.Web/Components/Shared/Refresh/Review/UpdateReviewScreen.razor.cs"));
        Assert.Contains("\"/js/unsaved-guard.js\"", writes);
        Assert.Contains("beforeunload", Read("src/Blocwerk.Web/wwwroot/js/unsaved-guard.js"));

        // "All as suggested" is the server's (it fills only what is unanswered there) and never offered for "not found".
        Assert.Contains("Reviews.AcceptSuggestedAsync(View.Id, g, panel)", writes);
        Assert.Contains("UpdateReviewService.AcceptsAll(bulk)", screen);
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
        var lists = Read("src/Blocwerk.Web/Components/Shared/Refresh/RefreshSummaryLists.razor");
        var app = Read("src/Blocwerk.Web/Components/BlocwerkApp.razor");

        Assert.Contains("@page \"/walls/{WallId:guid}/update\"", page);
        Assert.Contains("bwRefreshUpload.upload", drop);
        Assert.Contains("js/refresh-upload.js", app);

        // Apply is one button, which asks first and waits for a summary that is still being made.
        Assert.Contains("Disabled=\"@(View.Check3DPending || View.SummaryUpdating)\" OnConfirm=\"OnApply\" />", confirm);
        Assert.Contains("Question=\"@RefreshFlow.ApplyQuestion(s)\"", confirm);
        Assert.Equal(confirm.IndexOf("OnApply", StringComparison.Ordinal), confirm.LastIndexOf("OnApply", StringComparison.Ordinal));
        Assert.DoesNotContain("OnApply", lists);
        Assert.Contains("?update=review&amp;session=@View.UpdateSessionId\">Open full review</a>", lists);
    }

    [Fact]
    public void TheTimelineAndDiscard_AreBehindDetails_AndTheCheckStepIsOneScreen()
    {
        var page = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor");
        var css = Read("src/Blocwerk.Web/Components/Pages/Walls/WallRefresh.razor.css");

        var details = page.IndexOf("@if (detailsOpen && view is not null)", StringComparison.Ordinal);
        var timeline = page.IndexOf("<RefreshTimelineView View=\"view\" />", StringComparison.Ordinal);
        var discard = page.IndexOf("Discard this update</button>", StringComparison.Ordinal);
        var headerEnd = page.IndexOf("</header>", StringComparison.Ordinal);
        Assert.True(details > 0 && timeline > details && discard > timeline && headerEnd > discard, "the timeline and Discard belong inside the Details panel");
        Assert.Equal(timeline, page.LastIndexOf("<RefreshTimelineView", StringComparison.Ordinal));
        Assert.Contains("<UpdateSteps WallId=\"WallId\" Current=\"RefreshFlow.StepOf(view?.Status)\"", page);
        Assert.Contains(".refresh-page.is-one-screen { height: calc(var(--bw-app-h, 100dvh) - var(--topbar-h, 0px) - var(--bottomnav-h, 0px));", css);
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

    private static List<int> AllIndexesOf(string text, string value)
    {
        var found = new List<int>();
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + 1, StringComparison.Ordinal))
        {
            found.Add(at);
        }

        return found;
    }
}
