// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Blocwerk.Recognition.Worker.Jobs;

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary>
/// The runner API as this worker calls it (contracts <c>openapi/runner-api.yaml</c>). <c>401</c> throws
/// <see cref="UnauthorizedAccessException"/>, a job-scoped <c>404</c>/<c>410</c> <see cref="JobGoneException"/>, anything
/// else unsuccessful <see cref="HttpRequestException"/> (transient for the caller).
/// </summary>
public sealed class RunnerApiClient
{
    private readonly HttpClient http;

    public RunnerApiClient(HttpClient http, WorkerOptions options)
    {
        this.http = http;
        http.BaseAddress = options.Server;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Key);
        http.DefaultRequestHeaders.Add("X-Blocwerk-Claim", options.ClaimToken);
    }

    public async Task<RunnerHelloAnswer> HelloAsync(RunnerHello hello, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/runners/hello", hello, WorkerJson.Options, ct);
        Ensure(response, null);
        return await response.Content.ReadFromJsonAsync<RunnerHelloAnswer>(WorkerJson.Options, ct)
               ?? throw new HttpRequestException("Empty hello answer.");
    }

    /// <summary>The long-poll claim; null when the server had nothing (<c>204</c>).</summary>
    public async Task<RunnerClaim?> ClaimAsync(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/runners/claim", new { }, WorkerJson.Options, ct);
        Ensure(response, null);
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<RunnerClaim>(WorkerJson.Options, ct);
    }

    /// <summary>The input envelope, checked against the claim's size and SHA-256.</summary>
    public async Task<byte[]> BundleAsync(RunnerClaim claim, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/runners/jobs/{claim.JobId}/bundle", HttpCompletionOption.ResponseHeadersRead, ct);
        Ensure(response, claim.JobId);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.LongLength != claim.BundleBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != claim.BundleSha256)
        {
            throw new InvalidDataException($"The bundle of job {claim.JobId} does not match its size or SHA-256.");
        }

        return bytes;
    }

    public async Task ProgressAsync(Guid jobId, RunnerProgress progress, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/runners/jobs/{jobId}/progress", progress, WorkerJson.Options, ct);
        Ensure(response, jobId);
    }

    public async Task ResultAsync(Guid jobId, byte[] zip, CancellationToken ct)
    {
        using var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await http.PutAsync($"api/runners/jobs/{jobId}/result", content, ct);
        Ensure(response, jobId);
    }

    public async Task FailAsync(Guid jobId, RunnerFailure failure, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"api/runners/jobs/{jobId}/fail", failure, WorkerJson.Options, ct);
        Ensure(response, jobId);
    }

    /// <summary>Downloads a model by hash into <paramref name="target"/>; the caller checks the hash.</summary>
    public async Task DownloadModelAsync(string sha256, string target, CancellationToken ct)
    {
        using var response = await http.GetAsync($"api/runners/models/{sha256}", HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new RecognitionInputException($"The server has no model {sha256}.");
        }

        Ensure(response, null);
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file, ct);
    }

    private static void Ensure(HttpResponseMessage response, Guid? jobId)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                throw new UnauthorizedAccessException("The server refused the runner key (401).");
            case HttpStatusCode.NotFound or HttpStatusCode.Gone when jobId is { } id:
                throw new JobGoneException(id, (int)response.StatusCode);
            default:
                response.EnsureSuccessStatusCode();
                break;
        }
    }
}
