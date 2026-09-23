// <copyright file="CaptureVideoUploadEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;

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

        if (http.Request.ContentLength > options.MaxVideoBytes)
        {
            return Results.Problem(
                $"The video is larger than {options.MaxVideoBytes / (1024 * 1024)} MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            var info = await captures.AddVideoAsync(captureId, name, http.Request.Body, ct);
            return Results.Ok(info);
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
