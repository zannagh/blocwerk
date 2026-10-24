// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Pages;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using static Blocwerk.Core.Tests.OfflineClientStore.StoreWorld;
using static Blocwerk.Core.Tests.OfflineClientViews.ViewWorld;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>
/// The offline boulder detail (PR 3.11) from the device alone: the boulder, its holds on the right panel, the viewer's
/// own log (sent, flashed, attempts) and favourite, every comment newest first; a deleted comment gone after the next
/// sync and an erased author anonymised (both through the store, as the sync loop of PR 3.12 applies them), and the
/// kiosk class's unnamed members as "A member" with a plain avatar, as the Server shows them.
/// </summary>
public sealed class BoulderPageTests
{
    [Fact]
    public async Task ShowsTheBoulder_ItsHolds_AndTheViewersOwnLog()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Crimp}");
        var html = await RenderAsync(view, Crimp);

        Assert.Contains("<h1 title=\"Crimp Line\">Crimp Line</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"grade-badge\">6A</span>", html, StringComparison.Ordinal);
        Assert.Contains("by Alice", html, StringComparison.Ordinal);
        Assert.Contains("Oct 23, 2026", html, StringComparison.Ordinal);
        Assert.Contains("All red holds may be used as footholds.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("no such holds on this wall", html, StringComparison.Ordinal);
        Assert.Contains("No matching", html, StringComparison.Ordinal);
        Assert.Contains("1 start", html, StringComparison.Ordinal);
        Assert.Contains("1 top", html, StringComparison.Ordinal);
        Assert.Contains("1 foot only", html, StringComparison.Ordinal);
        Assert.Contains("7 ascents", html, StringComparison.Ordinal);
        Assert.Contains("(2 ratings)", html, StringComparison.Ordinal);

        // The panel with most of its holds (the left one: start and top), at the device's cached rung.
        Assert.Contains($"data-bw-panel=\"{PanelLeft}\"", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"/api/walls/{Wall}/a/{PanelSha('1')}/1920\"", html, StringComparison.Ordinal);
        Assert.Contains("panel-arrow right", html, StringComparison.Ordinal);
        Assert.Contains(WallViewPageTests.StartHold, html, StringComparison.Ordinal);
        Assert.DoesNotContain("cx=\"50.00\" cy=\"50.00\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("holds-all-tool", html, StringComparison.Ordinal);

        // Own log: three attempts, sent; a favourite.
        Assert.Contains($"data-bw-mine=\"{Crimp}\"", html, StringComparison.Ordinal);
        Assert.Contains("My stats: 3 attempts", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"chip send-chip\">Sent</span>", html, StringComparison.Ordinal);
        Assert.Contains("One of your favourites", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlash_ShowsAsFlashed()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Slab}");
        var html = await RenderAsync(view, Slab);

        Assert.Contains("My stats: 1 attempt<", html, StringComparison.Ordinal);
        Assert.Contains("<span class=\"chip flash-chip\">Flashed!</span>", html, StringComparison.Ordinal);
        Assert.Contains("archived-badge", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShareLink_ShowsNoMarks_AndLinksBackThroughItsToken()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(Anon), $"walls/{Wall}/boulders/{Crimp}/shared/tok");
        var html = await RenderAsync(view, Crimp, "tok");

        Assert.DoesNotContain("My stats", html, StringComparison.Ordinal);
        Assert.DoesNotContain("One of your favourites", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"walls/{Wall}/shared/tok\"", html, StringComparison.Ordinal);
        Assert.Contains($"/a/{PanelSha('1')}/1920?token=tok", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comments_ShowNewestFirst_WithTheirAuthors()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Crimp}");
        var html = await RenderAsync(view, Crimp);

        Assert.True(html.IndexOf("The top is spicy", StringComparison.Ordinal) < html.IndexOf("Lovely crimps", StringComparison.Ordinal));
        Assert.Contains("<span class=\"comment-author\">Bob</span>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"avatar avatar-sm\">B</div>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/users/", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedComment_IsGone_AfterTheNextSync()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Crimp}");
        Assert.Contains("The top is spicy", await RenderAsync(view, Crimp), StringComparison.Ordinal);

        // The stream's comment.deleted, through the store's feed applier (the sync loop's seam).
        view.Store.Apply(UserA, Envelope(FeedKinds.CommentDeleted, CommentB, Crimp));
        var html = await view.WaitForAsync(h => !h.Contains("The top is spicy", StringComparison.Ordinal));

        Assert.DoesNotContain("The top is spicy", html, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-bw-comment=\"{CommentB}\"", html, StringComparison.Ordinal);
        Assert.Contains("Lovely crimps", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedComment_IsGone_WhenTheNextCommentsDocumentReplacesTheOld()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(Anon), $"walls/{Wall}/boulders/{Crimp}/shared/tok");
        Assert.Contains("Lovely crimps", await RenderAsync(view, Crimp, "tok"), StringComparison.Ordinal);

        // A share device never gets comment.deleted for some boulders: the new sha replaces the set wholesale.
        view.Store.ReplaceDocument(Anon, Wall, DataDocumentKinds.Comments, Sha('d'), CommentsJson([Comments()[1]]));
        var html = await view.WaitForAsync(h => !h.Contains("Lovely crimps", StringComparison.Ordinal));

        Assert.DoesNotContain("Lovely crimps", html, StringComparison.Ordinal);
        Assert.Contains("The top is spicy", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErasedAuthor_IsAnonymised_AfterTheNextSync()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Crimp}");
        Assert.Contains("<span class=\"comment-author\">Bob</span>", await RenderAsync(view, Crimp), StringComparison.Ordinal);

        // Bob's erasure rebuilds the comments document: his comment now reads as the placeholder, his id is Ghost's.
        var ghost = new FeedActor(GhostUser.Id, PlaceholderIdentity.DisplayName, false, null);
        view.Store.ReplaceDocument(UserA, Wall, DataDocumentKinds.Comments, Sha('e'),
            CommentsJson([Comments()[0], CommentBy(CommentB, ghost, "The top is spicy", 5)]));
        var html = await view.WaitForAsync(h => !h.Contains(">Bob<", StringComparison.Ordinal));

        Assert.DoesNotContain("Bob", html, StringComparison.Ordinal);
        Assert.Contains($"<span class=\"comment-author\">{PlaceholderIdentity.DisplayName}</span>", html, StringComparison.Ordinal);
        Assert.Contains("The top is spicy", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheKioskClass_ShowsUnnamedMembers_AsAMember_WithAPlainAvatar()
    {
        // The kiosk class's document already masks a member without kiosk consent (KioskBylines); Alice consented.
        var masked = new FeedActor(null, KioskBylines.PlaceholderName, false, null);
        var device = CachedDevice(Kiosk, [CommentBy(CommentA, Alice with { Id = null }, "Lovely crimps", 0), CommentBy(CommentB, masked, "The top is spicy", 5)]);
        await using var view = await ViewHarness.StartAsync(device, $"walls/{Wall}/boulders/{Crimp}");
        var html = await RenderAsync(view, Crimp);

        Assert.Contains("<span class=\"comment-author\">A member</span>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"avatar avatar-sm\" aria-hidden=\"true\"></div>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"avatar avatar-sm\">A</div>", html, StringComparison.Ordinal);
        Assert.Equal(1, Count(html, "aria-hidden=\"true\"></div>"));
        Assert.DoesNotContain("Bob", html, StringComparison.Ordinal);
        Assert.DoesNotContain("My stats", html, StringComparison.Ordinal);
        Assert.Equal(Blocwerk.Client.Views.AuthorAvatar.UnnamedMember, KioskBylines.PlaceholderName);
    }

    [Fact]
    public async Task ABoulderTheDeviceDoesNotHold_SaysSo_WithTheServerPage()
    {
        var missing = Guid.NewGuid();
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{missing}");
        var html = await RenderAsync(view, missing);

        Assert.Contains("This boulder is not available offline", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"/walls/{Wall}/boulders/{missing}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABoulderDeletedByTheFeed_ShowsAsNotSaved()
    {
        await using var view = await ViewHarness.StartAsync(CachedDevice(UserA), $"walls/{Wall}/boulders/{Crimp}");
        await RenderAsync(view, Crimp);

        view.Store.Apply(UserA, Envelope(FeedKinds.BoulderDeleted, Crimp, Crimp));
        var html = await view.WaitForAsync(h => h.Contains("not available offline", StringComparison.Ordinal));

        Assert.Contains("This boulder is not available offline", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Lovely crimps", html, StringComparison.Ordinal);
    }

    private static int Count(string html, string what) => (html.Length - html.Replace(what, string.Empty, StringComparison.Ordinal).Length) / what.Length;

    private static Task<string> RenderAsync(ViewHarness view, Guid boulder, string? token = null) =>
        view.RenderAsync<BoulderPage>(new()
        {
            ["WallId"] = Wall,
            ["BoulderId"] = boulder,
            ["ShareToken"] = token,
        });
}
