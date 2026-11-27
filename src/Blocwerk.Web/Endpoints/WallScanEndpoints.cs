// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Capture.Replay;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The iPhone scan upload of the companion app, built like the capture import: <c>POST</c> the manifest (answers with the
/// files still missing), <c>PUT</c> each of them as the raw body (size and SHA-256 checked; a file already received is not
/// read again), <c>POST …/commit</c>, and <c>GET</c> the state. Under <c>/api/walls</c>, so a personal key's bearer reaches
/// the API-key handler; the policy then admits a signed-in browser user or a PERSONAL key with write access only (never a
/// wall key, kiosk key or installation key), and <see cref="IWallScanService"/> decides the wall: owner, admin or
/// moderator of it, or an administrator of the installation, never from a kiosk or a visitor link. Begin and commit take
/// <c>Content-Type: application/json</c> only (415 otherwise), so a cross-site form can never post one.
/// </summary>
public static class WallScanEndpoints
{
    public const string Prefix = "/api/walls/{wallId:guid}/scans";

    private static readonly MinDataRate MinBodyRate = new(bytesPerSecond: 16 * 1024, gracePeriod: TimeSpan.FromSeconds(30));

    public static void MapWallScans(this WebApplication app)
    {
        // Antiforgery is off like the other streamed uploads: the SameSite=Lax cookie is never sent on a cross-site
        // POST or PUT, a key caller has no antiforgery token to send, and the POSTs want a JSON content type, which a
        // cross-site form cannot send.
        var group = app.MapGroup(Prefix)
            .RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey)
            .DisableAntiforgery()
            .RequireWallScanRateLimit();
        group.MapPost(string.Empty, BeginAsync).RequireOneBeginPerUser();
        group.MapPut("{scanId:guid}/files/{**name}", PutFileAsync);
        group.MapPost("{scanId:guid}/commit", CommitAsync);
        group.MapGet("{scanId:guid}", (Guid wallId, Guid scanId, IWallScanService scans, CancellationToken ct) =>
            RunAsync(() => scans.GetAsync(wallId, scanId, ct)));
        group.MapDelete("{scanId:guid}", (Guid wallId, Guid scanId, IWallScanService scans, CancellationToken ct) =>
            RunAsync(() => scans.DeleteAsync(wallId, scanId, ct)));
    }

    internal static Task<IResult> BeginAsync(Guid wallId, HttpContext http, IWallScanService scans, CancellationToken ct)
    {
        SetBodyLimit(http, ScanPackageLimits.MaxManifestBytes);
        return RunAsync(async () =>
        {
            if (http.Request.ContentLength > ScanPackageLimits.MaxManifestBytes)
            {
                throw new ScanUploadException(ScanUploadException.TooLarge, "The manifest is too large.");
            }

            EnsureJson(http);

            // Who may upload, and whether the wall takes scans, is settled before a byte of the manifest is read.
            await scans.EnsureMayBeginAsync(wallId, ct);
            await using var body = new BoundedReadStream(http.Request.Body, ScanPackageLimits.MaxManifestBytes);
            var manifest = await JsonSerializer.DeserializeAsync<ScanManifest>(body, CapturePackageFiles.Json, ct)
                           ?? throw new JsonException("The body is empty.");
            return await scans.BeginAsync(wallId, manifest, ct);
        });
    }

    internal static Task<IResult> CommitAsync(Guid wallId, Guid scanId, HttpContext http, IWallScanService scans, CancellationToken ct) =>
        RunAsync(() =>
        {
            EnsureJson(http);
            return scans.CommitAsync(wallId, scanId, ct);
        });

    internal static Task<IResult> PutFileAsync(Guid wallId, Guid scanId, string name, HttpContext http, IWallScanService scans, CancellationToken ct)
    {
        SetBodyLimit(http, ScanPackageLimits.MaxVideoFileBytes);

        // Per-request rates are HTTP/1.x only (Kestrel throws for HTTP/2; Caddy proxies over HTTP/1.1).
        if (HttpProtocol.IsHttp11(http.Request.Protocol) && http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
        {
            rate.MinDataRate = MinBodyRate;
        }

        var sha256 = http.Request.Headers["X-Content-SHA256"].FirstOrDefault();
        return RunAsync(() => scans.PutFileAsync(wallId, scanId, name, sha256, http.Request.ContentLength, http.Request.Body, ct));
    }

    internal static async Task<IResult> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException)
        {
            return Error(StatusCodes.Status403Forbidden, "Only the wall's owner, admins and moderators may upload scans, and not from a wall tablet.");
        }
        catch (ScanUploadException ex)
        {
            return Error(ex.StatusCode, ex.Message, ex.Problems);
        }
        catch (JsonException ex)
        {
            return Error(StatusCodes.Status400BadRequest, $"The manifest is not readable JSON: {ex.Message}");
        }
        catch (BadHttpRequestException ex)
        {
            return Error(ex.StatusCode, "The upload was interrupted or too large.");
        }
        catch (UserFacingException ex)
        {
            return Error(StatusCodes.Status409Conflict, ex.Message);
        }
    }

    /// <summary>415 unless the request says it is JSON (an empty commit body too).</summary>
    private static void EnsureJson(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
        {
            throw new ScanUploadException(StatusCodes.Status415UnsupportedMediaType, "Send this request with Content-Type: application/json.");
        }
    }

    private static IResult Error(int status, string message, IReadOnlyList<string>? problems = null) =>
        Results.Json(new ScanErrorResponse(message, problems ?? []), statusCode: status);

    private static void SetBodyLimit(HttpContext http, long limit)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit + 1;
        }
    }
}
