using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Streaming upload for beta clips. The file is written straight to a temp file on the store's
/// volume (never held whole in memory), transcoded down toward the target when it is over the
/// store-as-is threshold, then committed and recorded. This replaces the old SignalR path, which
/// could not carry clips beyond the circuit's 64 MB message limit.
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

    private static async Task<IResult> HandleAsync(
        Guid boulderId,
        HttpContext http,
        IBetaVideoService betaVideoService,
        IBetaVideoStorage storage,
        IVideoTranscoder transcoder,
        BlocwerkSettings settings,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("BetaVideoUpload");
        var opts = settings.BetaVideo;

        // Decide first, write second. Permission is resolved from the authenticated principal and a
        // server-side lookup of the boulder's wall — nothing in the request body or its headers has a
        // say. Previously the only check lived in AddVideoFromFileAsync, which runs after the clip has
        // already been streamed to disk and possibly transcoded.
        try
        {
            await betaVideoService.EnsureCanUploadAsync(boulderId);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
        catch (InvalidOperationException)
        {
            return Results.Forbid();
        }

        var sizeFeature = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = opts.MaxUploadBytes;
        }

        if (string.IsNullOrEmpty(http.Request.ContentType)
            || !http.Request.ContentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Expected a multipart/form-data upload.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(
            MediaTypeHeaderValue.Parse(http.Request.ContentType).Boundary).Value;
        if (string.IsNullOrEmpty(boundary))
        {
            return Results.BadRequest("Malformed multipart upload.");
        }

        var reader = new MultipartReader(boundary, http.Request.Body);
        string? tempPath = null;
        string? contentType = null;
        string? fileName = null;
        byte[]? thumbnail = null;

        try
        {
            for (var section = await reader.ReadNextSectionAsync(cancellationToken);
                 section is not null;
                 section = await reader.ReadNextSectionAsync(cancellationToken))
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
                {
                    continue;
                }

                var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (isFile && disposition.Name.Value == "file")
                {
                    contentType = string.IsNullOrWhiteSpace(section.ContentType) ? "video/mp4" : section.ContentType;
                    fileName ??= disposition.FileNameStar.Value ?? disposition.FileName.Value;
                    tempPath = storage.CreateTempPath(Path.GetExtension(fileName ?? string.Empty));
                    await using var fs = File.Create(tempPath);
                    await section.Body.CopyToAsync(fs, cancellationToken);
                }
                else if (disposition.Name.Value == "thumbnail")
                {
                    using var ms = new MemoryStream();
                    await section.Body.CopyToAsync(ms, cancellationToken);
                    thumbnail = ms.Length > 0 ? ms.ToArray() : null;
                }
                else if (disposition.Name.Value == "fileName")
                {
                    using var sr = new StreamReader(section.Body);
                    fileName = await sr.ReadToEndAsync(cancellationToken);
                }
            }

            if (tempPath is null || !File.Exists(tempPath))
            {
                return Results.BadRequest("No video file part was sent.");
            }

            var size = new FileInfo(tempPath).Length;
            if (size == 0)
            {
                return Results.BadRequest("The video file is empty.");
            }

            if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Only video files can be uploaded as beta.");
            }

            var extension = Path.GetExtension(fileName ?? string.Empty);

            // Over the store-as-is threshold: re-encode down toward the target size.
            if (size > opts.StoreAsIsMaxBytes)
            {
                var outPath = storage.CreateTempPath(".mp4");
                var result = await transcoder.ShrinkAsync(tempPath, outPath, opts.TargetBytes, cancellationToken);
                File.Delete(tempPath);
                tempPath = outPath;
                contentType = result.ContentType;
                size = result.SizeBytes;
                extension = ".mp4";
                logger.LogInformation("Beta clip scaled down to {Bytes} bytes for boulder {BoulderId}", size, boulderId);
            }

            var storedName = storage.Commit(tempPath, extension);
            tempPath = null; // ownership moved into the store

            try
            {
                var info = await betaVideoService.AddVideoFromFileAsync(boulderId, storedName, size, contentType, thumbnail, fileName);
                return Results.Ok(info);
            }
            catch (UnauthorizedAccessException)
            {
                storage.Delete(storedName);
                return Results.Unauthorized();
            }
            catch (InvalidOperationException ex)
            {
                storage.Delete(storedName);
                return Results.BadRequest(ex.Message);
            }
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
            if (tempPath is not null && File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
