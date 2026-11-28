// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Streaming upload for beta clips. The file is written straight to a temp file on the store's
/// volume (never held whole in memory), committed verbatim, and recorded as
/// <c>Pending</c> — the background normalizer then turns it into a universally playable MP4 without
/// blocking this request. This replaces the old SignalR path, which could not carry clips beyond the
/// circuit's 64 MB message limit, and the old inline transcode, which held the request open for the
/// whole encode and only ran for clips over a size threshold.
/// </summary>
public static class BetaVideoUploadEndpoint
{
    public static void MapBetaVideoUpload(this WebApplication app)
    {
        // RequireAuthorization, not an in-handler check: the request must not reach the streaming
        // code at all without a principal. Antiforgery is disabled because the body is a streamed
        // multipart read by MultipartReader — the antiforgery middleware would have to buffer the
        // whole form to find its token, which is exactly what this endpoint exists to avoid. The
        // auth cookie is SameSite=Lax, so no cross-site POST carries it.
        app.MapPost("/api/beta-videos/{boulderId:guid}", HandleAsync)
            .RequireAuthorization()
            .DisableAntiforgery();
    }

    /// <summary>
    /// The upload itself, for the browser's route and the companion app's (which passes a smaller
    /// <paramref name="maxUploadBytes"/>): permission and quota first, then the streamed multipart read
    /// (<see cref="BetaVideoMultipart"/>), the quota again with the real size, the commit and the row. No exit leaves a
    /// temp file or an unrecorded stored file behind.
    /// </summary>
    internal static async Task<IResult> UploadAsync(
        Guid boulderId,
        HttpContext http,
        IBetaVideoService betaVideoService,
        IBetaVideoStorage storage,
        BetaVideoQuota quota,
        long maxUploadBytes,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("BetaVideoUpload");
        string? tempPath = null;
        try
        {
            // Decide first, write second. Permission is resolved from the authenticated principal and a
            // server-side lookup of the boulder's wall — nothing in the request body or its headers has a
            // say, and the quota is checked against the declared length before a byte is read.
            if (await RefuseBeforeReadingAsync(boulderId, http, betaVideoService, quota, maxUploadBytes, cancellationToken) is { } early)
            {
                return early;
            }

            var boundary = HeaderUtilities.RemoveQuotes(MediaTypeHeaderValue.Parse(http.Request.ContentType).Boundary).Value;
            if (string.IsNullOrEmpty(boundary))
            {
                return Results.BadRequest("Malformed multipart upload.");
            }

            var parts = await BetaVideoMultipart.ReadAsync(new MultipartReader(boundary, http.Request.Body), storage, cancellationToken);
            tempPath = parts.TempPath;
            if (parts.Error is not null)
            {
                return Results.BadRequest(parts.Error);
            }

            var size = new FileInfo(tempPath!).Length;
            if (await RefuseAfterReadingAsync(parts, size, quota, cancellationToken) is { } late)
            {
                return late;
            }

            var result = await StoreAsync(boulderId, parts, size, betaVideoService, storage);
            tempPath = null; // committed (and, on a failed record, deleted) by StoreAsync
            return result;
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta clip upload failed for boulder {BoulderId}", boulderId);
            return Results.Problem("The upload could not be processed.");
        }
        finally
        {
            BetaVideoMultipart.Discard(tempPath);
        }
    }

    // Permission, the quota on the declared length, the size cap and the body type: everything decided before reading.
    private static async Task<IResult?> RefuseBeforeReadingAsync(
        Guid boulderId,
        HttpContext http,
        IBetaVideoService betaVideoService,
        BetaVideoQuota quota,
        long maxUploadBytes,
        CancellationToken ct)
    {
        try
        {
            await betaVideoService.EnsureCanUploadAsync(boulderId);
        }
        catch (InvalidOperationException)
        {
            return Results.Forbid();
        }

        if (await quota.RefusalAsync(http.Request.ContentLength ?? 0, ct) is { } full)
        {
            return Results.Conflict(full);
        }

        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = maxUploadBytes;
        }

        return string.IsNullOrEmpty(http.Request.ContentType)
            || !http.Request.ContentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase)
            ? Results.BadRequest("Expected a multipart/form-data upload.")
            : null;
    }

    // An empty clip, a clip that is not a video, or one that does not fit the quota after all.
    private static async Task<IResult?> RefuseAfterReadingAsync(BetaVideoParts parts, long size, BetaVideoQuota quota, CancellationToken ct)
    {
        if (size == 0)
        {
            return Results.BadRequest("The video file is empty.");
        }

        if (string.IsNullOrWhiteSpace(parts.ContentType) || !parts.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Only video files can be uploaded as beta.");
        }

        return await quota.RefusalAsync(size, ct) is { } full ? Results.Conflict(full) : null;
    }

    // Stores the original verbatim and records the row; the background normalizer re-encodes it to a web-safe MP4 (or
    // fast-remuxes one that is already H.264/AAC) off the request thread. A failed record deletes the stored file again.
    private static async Task<IResult> StoreAsync(
        Guid boulderId,
        BetaVideoParts parts,
        long size,
        IBetaVideoService betaVideoService,
        IBetaVideoStorage storage)
    {
        var storedName = storage.Commit(parts.TempPath!, Path.GetExtension(parts.FileName ?? string.Empty));
        try
        {
            var info = await betaVideoService.AddVideoFromFileAsync(
                boulderId, storedName, size, parts.ContentType!, parts.Thumbnail, parts.FileName);
            return Results.Ok(info);
        }
        catch (InvalidOperationException ex)
        {
            storage.Delete(storedName);
            return Results.BadRequest(ex.Message);
        }
        catch
        {
            storage.Delete(storedName);
            throw;
        }
    }

    private static Task<IResult> HandleAsync(
        Guid boulderId,
        HttpContext http,
        IBetaVideoService betaVideoService,
        IBetaVideoStorage storage,
        BetaVideoQuota quota,
        BlocwerkSettings settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken) =>
        UploadAsync(boulderId, http, betaVideoService, storage, quota, settings.BetaVideo.MaxUploadBytes, loggerFactory, cancellationToken);
}
