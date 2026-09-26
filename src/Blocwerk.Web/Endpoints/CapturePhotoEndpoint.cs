// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Authentication.Authorization;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// One stored capture photo (metadata stripped), for the "Make sizes exact" picker: the admin taps two points on it.
/// Every gate lives in <see cref="IWallCaptureService.ReadPhotoAsync"/> (wall admin of the capture's wall, never a kiosk);
/// a signed-in human or a personal API key acting as its owner. Served privately, never cached by a shared cache.
/// </summary>
public static class CapturePhotoEndpoint
{
    public const string Route = "/api/captures/{captureId:guid}/photos/{photoId:guid}";

    /// <summary>The URL of a capture photo.</summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="photoId">The photo.</param>
    /// <returns>The route.</returns>
    public static string Url(Guid captureId, Guid photoId) => $"/api/captures/{captureId}/photos/{photoId}";

    public static void MapCapturePhotos(this WebApplication app)
    {
        app.MapGet(Route, HandleAsync).RequireAuthorization(BlocwerkPolicies.HumanOrUserApiKey);
    }

    internal static async Task<IResult> HandleAsync(
        Guid captureId, Guid photoId, HttpContext http, IWallCaptureService captures, CancellationToken ct)
    {
        try
        {
            var photo = await captures.ReadPhotoAsync(captureId, photoId, ct);
            http.Response.Headers.CacheControl = "private, max-age=600";
            return Results.File(photo.Bytes, photo.ContentType);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Forbid();
        }
        catch (KioskRestrictedException)
        {
            return Results.Forbid();
        }
        catch (InvalidOperationException)
        {
            // Not part of the capture, or its file is gone (photo retention).
            return Results.NotFound();
        }
    }
}
