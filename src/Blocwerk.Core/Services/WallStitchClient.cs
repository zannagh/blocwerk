using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Stitching;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <inheritdoc cref="IWallStitchClient"/>
public class WallStitchClient : IWallStitchClient
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient http;
    private readonly WallStitchSettings settings;
    private readonly ILogger<WallStitchClient> logger;

    public WallStitchClient(HttpClient http, BlocwerkSettings settings, ILogger<WallStitchClient> logger)
    {
        this.http = http;
        this.settings = settings.WallStitch;
        this.logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.BaseUrl);

    public async Task<StitchJobCreationResult> CreateJobAsync(
        IReadOnlyList<StitchPhotoUpload> photos,
        StitchJobOptions options,
        StitchPhotoUpload? oldPhoto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(options);

        using var response = await SendWithRetryAsync(
            () => BuildCreateRequest(photos, options, oldPhoto),
            HttpCompletionOption.ResponseContentRead,
            ct);

        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StitchJobCreationResult>(body, Json)
               ?? throw new InvalidOperationException("Stitch sidecar returned an empty job creation response.");
    }

    public async Task<StitchJobState> GetJobAsync(string sidecarJobId, CancellationToken ct = default)
    {
        var path = $"jobs/{Uri.EscapeDataString(sidecarJobId)}";
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path),
            HttpCompletionOption.ResponseContentRead,
            ct);

        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StitchJobState>(body, Json)
               ?? throw new InvalidOperationException($"Stitch sidecar returned an empty state for job {sidecarJobId}.");
    }

    public async Task DownloadArtifactAsync(
        string sidecarJobId,
        string artifactName,
        Stream destination,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var path = $"jobs/{Uri.EscapeDataString(sidecarJobId)}/artifacts/{Uri.EscapeDataString(artifactName)}";

        // ResponseHeadersRead + CopyToAsync: a full-resolution master is tens of megabytes and must
        // never be materialised as a byte[]. Do not "simplify" this to ReadAsByteArrayAsync.
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path),
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        await EnsureSuccessAsync(response, ct);
        await response.Content.CopyToAsync(destination, ct);
    }

    public async Task DeleteJobAsync(string sidecarJobId, CancellationToken ct = default)
    {
        var path = $"jobs/{Uri.EscapeDataString(sidecarJobId)}";
        using var response = await SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, path),
            HttpCompletionOption.ResponseContentRead,
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        await EnsureSuccessAsync(response, ct);
    }

    private HttpRequestMessage BuildCreateRequest(
        IReadOnlyList<StitchPhotoUpload> photos,
        StitchJobOptions options,
        StitchPhotoUpload? oldPhoto)
    {
        var content = new MultipartFormDataContent();
        foreach (var photo in photos)
        {
            content.Add(CreatePart(photo), "photos", photo.FileName);
        }

        var optionsJson = new StringContent(JsonSerializer.Serialize(options, Json), Encoding.UTF8, "application/json");
        content.Add(optionsJson, "options");

        if (oldPhoto is not null)
        {
            content.Add(CreatePart(oldPhoto), "oldPhoto", oldPhoto.FileName);
        }

        return new HttpRequestMessage(HttpMethod.Post, "jobs") { Content = content };
    }

    private static ByteArrayContent CreatePart(StitchPhotoUpload photo)
    {
        var part = new ByteArrayContent(photo.Content);
        part.Headers.ContentType = new MediaTypeHeaderValue(photo.ContentType);
        return part;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("No stitch sidecar is configured (Blocwerk:WallStitch:BaseUrl).");
        }

        var attempts = Math.Max(0, settings.MaxRetries) + 1;
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await http.SendAsync(requestFactory(), completionOption, ct);
                if (!IsTransient(response.StatusCode) || attempt >= attempts)
                {
                    return response;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= attempts)
                {
                    throw;
                }

                logger.LogWarning(ex, "Stitch sidecar call failed (attempt {Attempt}/{Attempts}); retrying", attempt, attempts);
            }

            response?.Dispose();
            await Task.Delay(BackoffFor(attempt), ct);
        }
    }

    private TimeSpan BackoffFor(int attempt) =>
        TimeSpan.FromMilliseconds(settings.RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

    private static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests;

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadAsStringAsync(ct)
            : string.Empty;

        throw new HttpRequestException(
            $"Stitch sidecar returned {(int)response.StatusCode} {response.ReasonPhrase}. {detail}".TrimEnd(),
            inner: null,
            response.StatusCode);
    }
}
