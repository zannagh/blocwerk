// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.WhatsNew;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Stands in for api.github.com behind <see cref="GitHubReleaseNotesService"/>: answers from a function
/// and records every request, so no test ever reaches the real GitHub.
/// </summary>
internal sealed class FakeGitHubReleases : HttpMessageHandler, IHttpClientFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> respond;

    public FakeGitHubReleases(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        this.respond = respond;
    }

    public List<HttpRequestMessage> Requests { get; } = [];

    public static HttpResponseMessage Json(object releases, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }

    public static object Release(string tag, bool prerelease, string body = "", int day = 1)
    {
        return new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["name"] = tag,
            ["body"] = body,
            ["prerelease"] = prerelease,
            ["draft"] = false,
            ["published_at"] = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero),
            ["html_url"] = $"https://github.com/zannagh/blocwerk/releases/tag/{tag}",
        };
    }

    public GitHubReleaseNotesService CreateService(TimeProvider time, Action<ReleaseNotesOptions>? configure = null)
    {
        var options = new ReleaseNotesOptions { ApiBaseUrl = "https://github.test" };
        configure?.Invoke(options);
        return new GitHubReleaseNotesService(
            this,
            Options.Create(options),
            NullLogger<GitHubReleaseNotesService>.Instance,
            time);
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(this, disposeHandler: false);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}
