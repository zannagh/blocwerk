// <copyright file="CaptureVideoUploadEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Streaming upload of a capture draft's optional walk-along video (photo-real view only). The raw
/// request body IS the file (<c>?name=</c> carries its name); it is streamed straight into the
/// capture store, never held whole in memory and never over the SignalR circuit. Every gate lives in
/// <see cref="IWallCaptureService.AddVideoAsync"/> (wall admin, not a kiosk, own open draft, glyphs
/// on, splat worker configured) and runs BEFORE the first byte is written.
/// </summary>
public static class CaptureVideoUploadEndpoint
{
    /// <summary>
    /// Longest an upload may stream. The browser gives up after 120 minutes (<c>capture-video.js</c>);
    /// the server must not wait longer, or a client trickling bytes holds an upload slot, a disk file
    /// and the deploy busy gate for as long as it likes.
    /// </summary>
    public static readonly TimeSpan MaxUploadDuration = TimeSpan.FromMinutes(125);

    /// <summary>
    /// Slowest body accepted, after a grace period (Kestrel's default is 240 B/s — ~100 days for 2 GB).
    /// 16 KB/s is far below any connection that could finish a real walk video inside the client's hour.
    /// </summary>
    private static readonly MinDataRate MinBodyRate = new(bytesPerSecond: 16 * 1024, gracePeriod: TimeSpan.FromSeconds(30));

    public static void MapCaptureVideoUpload(this WebApplication app)
    {
        // Antiforgery is off for the same reason as the beta-video upload: the body is streamed, and
        // the auth cookie is SameSite=Lax, so no cross-site POST carries it.
        app.MapPost("/api/captures/{captureId:guid}/video", HandleAsync)
            .RequireAuthorization()
            .DisableAntiforgery();
    }

    private static async Task<IResult> HandleAsync(
        Guid captureId,
        string? name,
        HttpContext http,
        IWallCaptureService captures,
        WallCapturePipelineOptions options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            // The service enforces the exact cap while streaming; this only lets such a body in.
            sizeFeature.MaxRequestBodySize = options.MaxVideoBytes + 1;
        }

        // Per-request rates are HTTP/1.x only (Kestrel throws for HTTP/2; Caddy proxies over HTTP/1.1).
        if (HttpProtocol.IsHttp11(http.Request.Protocol)
            && http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rateFeature)
        {
            rateFeature.MinDataRate = MinBodyRate;
        }

        if (http.Request.ContentLength > options.MaxVideoBytes)
        {
            return Results.Problem(
                $"The video is larger than {options.MaxVideoBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(MaxUploadDuration);
        try
        {
            var info = await captures.AddVideoAsync(captureId, name, http.Request.Body, deadline.Token);
            return Results.Ok(info);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Results.Problem(
                $"The upload took longer than {MaxUploadDuration.TotalMinutes:0} minutes.", statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (KioskRestrictedException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }
        catch (BadHttpRequestException ex)
        {
            loggerFactory.CreateLogger("CaptureVideoUpload").LogInformation(ex, "Capture video upload for {CaptureId} aborted", captureId);
            return Results.BadRequest("The upload was interrupted.");
        }
    }
}
