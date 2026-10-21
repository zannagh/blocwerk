// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Blocwerk.Authentication.Authorization;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Progress and results of the on-device storage probe (<c>/storage-probe</c>): what a phone's browser
/// reports about persistence, quota, OPFS and Cache API limits, and how far a size step got before the page
/// died. Nothing is stored; each report becomes ONE Information line prefixed <see cref="LogPrefix"/>, so a
/// run can be followed with <c>docker logs ... | grep STORAGE-PROBE</c> (no browser console on an iPhone).
/// Signed-in browsers only, small JSON bodies only, rate-limited per user.
/// </summary>
public static partial class StorageProbeEndpoints
{
    public const string Route = "/api/probe/storage";

    public const string LogPrefix = "STORAGE-PROBE";

    /// <summary>Largest body accepted; the client trims its payload to fit.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>Reports per user per <see cref="Window"/>: a full size ladder is about 130.</summary>
    public const int MaxReports = 200;

    /// <summary>Longest serialized <c>data</c> logged; anything longer is cut and marked.</summary>
    public const int MaxDataChars = 2048;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, ReportWindow> Recent = new();

    // Relaxed: quotes and </> stay readable in the log. It still escapes every control character, which is
    // what keeps a report on one line (StorageProbeEndpointTests pins that for \n and NUL).
    private static readonly JsonSerializerOptions DataJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void MapStorageProbe(this WebApplication app)
    {
        // Antiforgery is off like the photo-real diagnostics: the body must be JSON (a cross-site form cannot
        // send that without a CORS preflight, and the app has no CORS policy), and the auth cookie is
        // SameSite=Lax, so no cross-site POST carries it. A forged report could only add a log line anyway.
        app.MapPost(Route, HandleAsync)
            .RequireAuthorization()
            .DenyApiKeyPrincipals()
            .DisableAntiforgery();
    }

    internal static async Task<IResult> HandleAsync(HttpContext context, ClaimsPrincipal user, ILoggerFactory loggerFactory)
    {
        var request = context.Request;
        if (!request.HasJsonContentType())
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadCappedAsync(request.Body, context.RequestAborted);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // No id, no bucket: such callers would otherwise share one window and starve each other.
        var key = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(key))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!Admit(key, DateTimeOffset.UtcNow))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var report = Parse(body);
        if (report is null)
        {
            return Results.BadRequest();
        }

        loggerFactory.CreateLogger("Blocwerk.StorageProbe").LogInformation(
            LogPrefix + " run={Run:l} ctx={Context:l} seq={Seq} step={Step:l} result={Result:l} data={Data:l}",
            report.Run, report.Context, report.Seq, report.Step, report.Result, report.Data);
        return Results.NoContent();
    }

    /// <summary>
    /// The report, with its identifiers checked against a token pattern and its data re-serialized compactly,
    /// so a client cannot break the one-line-per-report shape or forge a second line. Null when malformed.
    /// </summary>
    internal static StorageProbeLine? Parse(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var run = Token(root, "run");
            var context = Token(root, "ctx");
            var step = Token(root, "step");
            var result = Token(root, "result");
            if (run is null || context is null || step is null || result is null)
            {
                return null;
            }

            var seq = root.TryGetProperty("seq", out var s) && s.TryGetInt32(out var n) ? n : 0;
            var data = root.TryGetProperty("data", out var d) ? JsonSerializer.Serialize(d, DataJson) : "{}";
            if (data.Length > MaxDataChars)
            {
                data = data[..MaxDataChars] + $"...(cut, {data.Length} chars)";
            }

            return new StorageProbeLine(run, context, seq, step, result, data);
        }
    }

    /// <summary>Sliding-window admission per user; idle users are pruned once the map grows past 1000.</summary>
    internal static bool Admit(string key, DateTimeOffset now)
    {
        while (true)
        {
            var entry = Recent.GetOrAdd(key, _ => new ReportWindow());
            lock (entry)
            {
                if (entry.Retired)
                {
                    // Pruned between GetOrAdd and the lock: take the fresh window instead.
                    continue;
                }

                while (entry.Times.Count > 0 && now - entry.Times.Peek() > Window)
                {
                    entry.Times.Dequeue();
                }

                if (entry.Times.Count >= MaxReports)
                {
                    return false;
                }

                entry.Times.Enqueue(now);
            }

            Prune(now);
            return true;
        }
    }

    private static void Prune(DateTimeOffset now)
    {
        if (Recent.Count <= 1000)
        {
            return;
        }

        foreach (var (k, entry) in Recent)
        {
            lock (entry)
            {
                // Removed only while locked and only this exact instance, so a live window is never dropped.
                if (entry.IsIdle(now, Window) && Recent.TryRemove(new KeyValuePair<string, ReportWindow>(k, entry)))
                {
                    entry.Retired = true;
                }
            }
        }
    }

    /// <summary>The body, or null once it runs past <see cref="MaxBodyBytes"/> (a chunked body has no length).</summary>
    private static async Task<byte[]?> ReadCappedAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await body.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                return buffer[..total];
            }

            total += read;
        }

        return null;
    }

    private static string? Token(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return text is not null && TokenPattern().IsMatch(text) ? text : null;
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,80}$")]
    private static partial Regex TokenPattern();
}
