using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Blocwerk.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Compute;

/// <summary>
/// <see cref="IComputeJobClient"/> over one <see cref="HttpClient"/> (a named client per worker, so each
/// has its own base address and timeout). The key rides on each request's <c>Authorization</c>
/// header only; it never appears in an exception message or a log line.
/// </summary>
public sealed class ComputeJobClient(
    ComputeServiceKind service,
    HttpClient http,
    ComputeServiceSettings settings,
    ILogger<ComputeJobClient> logger) : IComputeJobClient
{
    public ComputeServiceKind Service { get; } = service;

    public bool IsConfigured => settings.IsConfigured;

    public async Task<ComputeHealth> GetHealthAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("health"));
        var body = await SendAsync(request, "check the service", authenticate: false, ct);
        return Parse<ComputeHealth>(body);
    }

    public async Task<string> SubmitJsonAsync(string kind, string json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"v1/jobs/{Uri.EscapeDataString(kind)}"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        return JobId(await SendAsync(request, $"start the {kind} job", authenticate: true, ct));
    }

    public async Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        foreach (var part in parts)
        {
            var bytes = new ByteArrayContent(part.Content);
            bytes.Headers.ContentType = new MediaTypeHeaderValue(part.ContentType);
            if (part.FileName is null)
            {
                content.Add(bytes, part.Name);
            }
            else
            {
                content.Add(bytes, part.Name, part.FileName);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Url($"v1/jobs/{Uri.EscapeDataString(kind)}"))
        {
            Content = content,
        };
        return JobId(await SendAsync(request, $"start the {kind} job", authenticate: true, ct));
    }

    public async Task<ComputeJobStatus> GetStatusAsync(string jobId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url($"v1/jobs/{Uri.EscapeDataString(jobId)}"));
        return Parse<ComputeJobStatus>(await SendAsync(request, "read the job status", authenticate: true, ct));
    }

    public async Task<byte[]> DownloadFileAsync(string jobId, string name, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, Url($"v1/jobs/{Uri.EscapeDataString(jobId)}/files/{Uri.EscapeDataString(name)}"));
        using var response = await SendRawAsync(request, $"download {name}", authenticate: true, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<byte[]> DownloadFileAsync(string jobId, string name, long maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, Url($"v1/jobs/{Uri.EscapeDataString(jobId)}/files/{Uri.EscapeDataString(name)}"));
        using var response = await SendRawAsync(request, $"download {name}", authenticate: true, ct);
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw ComputeJobException.TooLarge(name, maxBytes);
        }

        // Read at most one byte past the limit, whatever the header claimed.
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw ComputeJobException.TooLarge(name, maxBytes);
            }
        }

        return buffer.ToArray();
    }

    public async Task CancelAsync(string jobId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, Url($"v1/jobs/{Uri.EscapeDataString(jobId)}"));
            await SendAsync(request, "cancel the job", authenticate: true, ct);
        }
        catch (ComputeJobException ex)
        {
            logger.LogInformation("Cancelling {Service} job {JobId} failed: {Reason}", Service, jobId, ex.Message);
        }
    }

    private Uri Url(string path)
    {
        if (!settings.IsConfigured)
        {
            throw new ComputeJobException(
                ComputeFailureKind.NotConfigured,
                settings.ConfigurationError ?? "The 3D computation service is not configured on this server.");
        }

        return new Uri(new Uri(settings.Url!.TrimEnd('/') + "/"), path);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, string action, bool authenticate, CancellationToken ct)
    {
        using var response = await SendRawAsync(request, action, authenticate, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request, string action, bool authenticate, CancellationToken ct)
    {
        if (authenticate && !string.IsNullOrEmpty(settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ComputeJobException(
                ComputeFailureKind.Unavailable, $"The 3D computation service did not answer in time (trying to {action}).", ex);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{Service} worker unreachable trying to {Action}: {Error}", Service, action, ex.Message);
            throw new ComputeJobException(
                ComputeFailureKind.Unavailable, $"The 3D computation service could not be reached (trying to {action}).", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var detail = await ReadDetailAsync(response, ct);
            logger.LogWarning(
                "{Service} worker answered {Status} trying to {Action}: {Detail}", Service, (int)response.StatusCode, action, detail);
            var jobScoped = request.Method != HttpMethod.Post;
            throw ComputeJobErrors.FromStatus(response.StatusCode, action, detail, jobScoped);
        }
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The job id of a submit answer. It is stored in a varchar(128): a longer one is a protocol error.</summary>
    private static string JobId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("jobId", out var id) && id.GetString() is { Length: > 0 and <= 128 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Falls through to the protocol error below.
        }

        throw new ComputeJobException(ComputeFailureKind.Protocol, "The 3D computation service answered without a job id.");
    }

    private static T Parse<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body)
                   ?? throw new ComputeJobException(ComputeFailureKind.Protocol, "The 3D computation service sent an empty answer.");
        }
        catch (JsonException ex)
        {
            throw new ComputeJobException(ComputeFailureKind.Protocol, "The 3D computation service sent an unreadable answer.", ex);
        }
    }
}
