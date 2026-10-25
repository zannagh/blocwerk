// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blocwerk.Core.WhatsNew;

/// <summary>
/// Reads the public repository's releases anonymously and keeps them in memory for an hour.
/// </summary>
/// <remarks>
/// Unauthenticated GitHub calls are limited to 60 an hour per IP, so the list is fetched at most once
/// per <see cref="ReleaseNotesOptions.CacheDuration"/> for the whole app, revalidated with the first
/// page's ETag (a 304 does not count against the limit), and a failure is remembered for
/// <see cref="ReleaseNotesOptions.FailureBackoff"/> rather than retried on every page view. A failure
/// keeps serving the last good list. One fetch, every page included, is bounded by
/// <see cref="ReleaseNotesOptions.Timeout"/>.
/// </remarks>
public sealed partial class GitHubReleaseNotesService : IReleaseNotesService, IDisposable
{
    /// <summary>The named HttpClient this service reads GitHub through.</summary>
    public const string HttpClientName = "github-releases";

    private const int PageSize = 100;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ReleaseNotesOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<GitHubReleaseNotesService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string repository;
    private readonly string htmlBaseUrl;
    private IReadOnlyList<ReleaseNote>? cached;
    private string? etag;
    private DateTimeOffset nextRefreshAt = DateTimeOffset.MinValue;

    public GitHubReleaseNotesService(
        IHttpClientFactory clientFactory,
        IOptions<ReleaseNotesOptions> releaseOptions,
        ILogger<GitHubReleaseNotesService> serviceLogger,
        TimeProvider? timeProvider = null)
    {
        httpClientFactory = clientFactory;
        options = releaseOptions.Value;
        logger = serviceLogger;
        time = timeProvider ?? TimeProvider.System;
        repository = RepositoryPattern().IsMatch(options.Repository ?? string.Empty)
            ? options.Repository!
            : ReleaseNotesOptions.DefaultRepository;
        htmlBaseUrl = string.IsNullOrWhiteSpace(options.HtmlBaseUrl) ? "https://github.com" : options.HtmlBaseUrl.TrimEnd('/');
    }

    private string RepositoryHtmlUrl => $"{htmlBaseUrl}/{repository}";

    /// <inheritdoc />
    public async Task<ReleaseFeed> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Snapshot();
        }

        try
        {
            if (time.GetUtcNow() >= nextRefreshAt)
            {
                await RefreshAsync(cancellationToken);
            }

            return Snapshot();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        gate.Dispose();
    }

    private ReleaseFeed Snapshot()
    {
        return new ReleaseFeed(cached ?? [], cached is not null, $"{RepositoryHtmlUrl}/releases", RepositoryHtmlUrl);
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            timeout.CancelAfter(options.Timeout);
            var fetched = await FetchAsync(timeout.Token);
            if (fetched is not null)
            {
                cached = fetched;
            }

            nextRefreshAt = time.GetUtcNow() + options.CacheDuration;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller going away is not GitHub failing: leave the next view free to try again.
        }
        catch (Exception ex)
        {
            // Anything at all (GitHub down or rate-limited, a timeout, bad JSON, a malformed ApiBaseUrl) is the
            // fallback, never an error page: back off and keep serving what there is.
            nextRefreshAt = time.GetUtcNow() + options.FailureBackoff;
            logger.LogWarning(ex, "Could not read the release notes of {Repository} from GitHub", repository);
        }
    }

    /// <summary>The releases newest first, or null when GitHub says the cached list is still current.</summary>
    private async Task<List<ReleaseNote>?> FetchAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var releases = new List<ReleaseNote>();
        string? firstPageEtag = null;
        for (var page = 1; page <= Math.Max(1, options.MaxPages); page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PageUrl(page));
            if (page == 1 && cached is not null && EntityTagHeaderValue.TryParse(etag, out var known))
            {
                request.Headers.IfNoneMatch.Add(known);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (page == 1 && response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"GitHub answered {(int)response.StatusCode}", null, response.StatusCode);
            }

            firstPageEtag ??= response.Headers.ETag?.ToString();
            var batch = await response.Content.ReadFromJsonAsync<List<GitHubReleaseDto>>(cancellationToken) ?? [];
            releases.AddRange(batch.Where(d => !d.Draft && !string.IsNullOrWhiteSpace(d.TagName)).Select(ToNote));

            // Every deploy publishes a prerelease, so the last stable release can sit pages down.
            if (batch.Count < PageSize || releases.Any(r => !r.IsPrerelease))
            {
                break;
            }
        }

        etag = firstPageEtag;
        return releases.OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue).ToList();
    }

    private Uri PageUrl(int page)
    {
        return new Uri($"{options.ApiBaseUrl.TrimEnd('/')}/repos/{repository}/releases?per_page={PageSize}&page={page}");
    }

    private ReleaseNote ToNote(GitHubReleaseDto dto)
    {
        var tag = dto.TagName!.Trim();
        var htmlUrl = ReleaseMarkdown.SafeUrl(dto.HtmlUrl) ?? $"{RepositoryHtmlUrl}/releases/tag/{Uri.EscapeDataString(tag)}";
        var name = string.IsNullOrWhiteSpace(dto.Name) ? tag : dto.Name.Trim();
        return new ReleaseNote(tag, name, dto.PublishedAt, dto.Prerelease, htmlUrl, ReleaseMarkdown.ToSafeHtml(dto.Body));
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
}
