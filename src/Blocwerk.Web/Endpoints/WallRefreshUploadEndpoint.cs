// <copyright file="WallRefreshUploadEndpoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The "Update panels + 3D" drop zone: one file per request as the raw body (refresh-upload.js), never over the
/// Blazor circuit. A photo goes into the run's capture draft, a video is kept for the 3D capture. A refused file
/// is answered 200 with its reason, so the page lists it and the next file still goes up.
/// </summary>
public static class WallRefreshUploadEndpoint
{
    public const string Route = "/api/refreshes/{refreshId:guid}/files";

    public static string Url(Guid refreshId) => $"/api/refreshes/{refreshId}/files";

    public static void MapWallRefreshUpload(this WebApplication app)
    {
        // Antiforgery is off like the capture video upload: a streamed body, and the SameSite=Lax cookie is
        // never sent on a cross-site POST.
        app.MapPost(Route, HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey)
            .DisableAntiforgery();
    }

    /// <summary>The refusal for a file over the limit, in the user's words.</summary>
    public static string TooLarge(bool isVideo, long limit) =>
        $"The {(isVideo ? "video" : "photo")} is larger than {limit / (1024 * 1024)} MB.";

    internal static async Task<IResult> HandleAsync(
        Guid refreshId, string? name, HttpContext http, IWallRefreshService refreshes, WallCapturePipelineOptions options, CancellationToken ct)
    {
        var isVideo = CaptureVideoFiles.Extensions.Contains(Path.GetExtension(name ?? string.Empty));
        var limit = isVideo ? options.MaxVideoBytes : options.MaxPhotoBytes;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = limit + 1;
        }

        try
        {
            // Who may add what is settled before a byte of the body is read.
            await refreshes.EnsureCanUploadAsync(refreshId, isVideo);
            if (http.Request.ContentLength > limit)
            {
                return Results.Ok(new RefreshFile(null, name, isVideo, TooLarge(isVideo, limit)));
            }

            if (isVideo)
            {
                await refreshes.AddVideoAsync(refreshId, name, http.Request.Body, ct);
                return Results.Ok(new RefreshFile(null, name, true, null));
            }

            var bytes = await ReadCappedAsync(http.Request.Body, limit, ct);
            var photo = await refreshes.AddPhotoAsync(refreshId, name, bytes, ct);
            return Results.Ok(new RefreshFile(photo.PhotoId, name, false, null));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or KioskRestrictedException)
        {
            return Results.Forbid();
        }
        catch (UserFacingException ex)
        {
            return Results.Ok(new RefreshFile(null, name, isVideo, ex.Message));
        }
        catch (BadHttpRequestException ex)
        {
            var problem = ex.StatusCode == StatusCodes.Status413PayloadTooLarge ? TooLarge(isVideo, limit) : "The upload was interrupted.";
            return Results.Ok(new RefreshFile(null, name, isVideo, problem));
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream body, long limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new UserFacingException($"The photo is larger than {limit / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
