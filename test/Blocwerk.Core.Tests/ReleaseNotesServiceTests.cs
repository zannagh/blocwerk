// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Core.WhatsNew;

namespace Blocwerk.Core.Tests;

public class ReleaseNotesServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fetches_newest_first_from_the_configured_repository_anonymously()
    {
        using var github = new FakeGitHubReleases(_ => FakeGitHubReleases.Json(new[]
        {
            FakeGitHubReleases.Release("v0.1.0", false, day: 1),
            FakeGitHubReleases.Release("0.1.1-224", true, day: 24),
        }));
        using var service = github.CreateService(new MutableTestClock(Start), o => o.Repository = "someone/fork");

        var feed = await service.GetAsync();

        Assert.True(feed.Available);
        Assert.Equal(["0.1.1-224", "v0.1.0"], feed.Releases.Select(r => r.Tag));
        Assert.Equal("https://github.test/repos/someone/fork/releases?per_page=100&page=1", github.Requests[0].RequestUri!.ToString());
        Assert.Null(github.Requests[0].Headers.Authorization);
        Assert.Equal("https://github.com/someone/fork/releases", feed.ReleasesPageUrl);
    }

    [Fact]
    public async Task Serves_the_cache_for_the_hour_and_revalidates_with_the_etag_after()
    {
        var clock = new MutableTestClock(Start);
        var calls = 0;
        using var github = new FakeGitHubReleases(request =>
        {
            calls++;
            return request.Headers.IfNoneMatch.Count > 0
                ? new HttpResponseMessage(HttpStatusCode.NotModified)
                : FakeGitHubReleases.Json(new[] { FakeGitHubReleases.Release("v0.1.0", false) }, "\"abc\"");
        });
        using var service = github.CreateService(clock);

        await service.GetAsync();
        clock.Advance(TimeSpan.FromMinutes(59));
        await service.GetAsync();
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(2));
        var feed = await service.GetAsync();

        Assert.Equal(2, calls);
        Assert.Equal("\"abc\"", github.Requests[1].Headers.IfNoneMatch.Single().ToString());
        Assert.Equal("v0.1.0", Assert.Single(feed.Releases).Tag);
    }

    [Fact]
    public async Task A_github_failure_is_an_unavailable_feed_with_the_link_and_is_not_retried_at_once()
    {
        var clock = new MutableTestClock(Start);
        using var github = new FakeGitHubReleases(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var service = github.CreateService(clock);

        var feed = await service.GetAsync();
        await service.GetAsync();

        Assert.False(feed.Available);
        Assert.Empty(feed.Releases);
        Assert.Equal("https://github.com/zannagh/blocwerk/releases", feed.ReleasesPageUrl);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task A_failure_after_a_good_fetch_keeps_serving_the_last_good_list()
    {
        var clock = new MutableTestClock(Start);
        var fail = false;
        using var github = new FakeGitHubReleases(_ => fail
            ? throw new HttpRequestException("network down")
            : FakeGitHubReleases.Json(new[] { FakeGitHubReleases.Release("v0.1.0", false) }));
        using var service = github.CreateService(clock);
        await service.GetAsync();

        fail = true;
        clock.Advance(TimeSpan.FromHours(2));
        var feed = await service.GetAsync();

        Assert.True(feed.Available);
        Assert.Equal("v0.1.0", Assert.Single(feed.Releases).Tag);
    }

    [Fact]
    public async Task Malformed_json_and_a_hung_github_both_end_as_an_unavailable_feed()
    {
        using var garbage = new FakeGitHubReleases(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>"),
        });
        using var garbageService = garbage.CreateService(new MutableTestClock(Start));
        Assert.False((await garbageService.GetAsync()).Available);

        using var hung = new HungGitHub();
        using var hungService = new GitHubReleaseNotesService(
            hung,
            Microsoft.Extensions.Options.Options.Create(new ReleaseNotesOptions { Timeout = TimeSpan.FromMilliseconds(200) }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitHubReleaseNotesService>.Instance);
        var feed = await hungService.GetAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(feed.Available);
    }

    [Fact]
    public async Task Pages_on_only_until_a_stable_release_shows_up()
    {
        using var github = new FakeGitHubReleases(request =>
        {
            var page = request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal) ? 1 : 2;
            var tags = page == 1
                ? Enumerable.Range(0, 100).Select(i => FakeGitHubReleases.Release($"0.1.1-{200 - i}", true, day: 20))
                : [FakeGitHubReleases.Release("v0.1.0", false, day: 1)];
            return FakeGitHubReleases.Json(tags.ToArray());
        });
        using var service = github.CreateService(new MutableTestClock(Start));

        var feed = await service.GetAsync();

        Assert.Equal(2, github.Requests.Count);
        Assert.Equal(101, feed.Releases.Count);
        Assert.Equal("v0.1.0", feed.Releases[^1].Tag);
    }

    [Fact]
    public async Task Drafts_and_tagless_entries_are_dropped_and_a_malformed_repository_falls_back_to_the_default()
    {
        var draft = new Dictionary<string, object?> { ["tag_name"] = "v9", ["draft"] = true };
        var tagless = new Dictionary<string, object?> { ["name"] = "no tag" };
        using var github = new FakeGitHubReleases(_ => FakeGitHubReleases.Json(new object[]
        {
            draft, tagless, FakeGitHubReleases.Release("v0.1.0", false),
        }));
        using var service = github.CreateService(new MutableTestClock(Start), o => o.Repository = "../../evil?x=");

        var feed = await service.GetAsync();

        Assert.Equal("v0.1.0", Assert.Single(feed.Releases).Tag);
        Assert.Contains("/repos/zannagh/blocwerk/releases", github.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a url at all")]
    [InlineData("::bad::")]
    public async Task A_malformed_api_base_url_is_the_fallback_not_an_exception(string apiBaseUrl)
    {
        var clock = new MutableTestClock(Start);
        using var github = new FakeGitHubReleases(_ => FakeGitHubReleases.Json(Array.Empty<object>()));
        using var service = github.CreateService(clock, o => o.ApiBaseUrl = apiBaseUrl);

        var feed = await service.GetAsync();

        Assert.False(feed.Available);
        Assert.Equal("https://github.com/zannagh/blocwerk/releases", feed.ReleasesPageUrl);
        Assert.Empty(github.Requests);
    }

    [Fact]
    public async Task Any_unexpected_exception_is_the_fallback_and_backs_off()
    {
        var clock = new MutableTestClock(Start);
        var calls = 0;
        using var github = new FakeGitHubReleases(_ =>
        {
            calls++;
            throw new InvalidOperationException("something nobody expected");
        });
        using var service = github.CreateService(clock, o => o.HtmlBaseUrl = null!);

        var feed = await service.GetAsync();
        await service.GetAsync();
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(6));
        await service.GetAsync();

        Assert.False(feed.Available);
        Assert.Equal("https://github.com/zannagh/blocwerk/releases", feed.ReleasesPageUrl);
        Assert.Equal(2, calls);
    }
}
