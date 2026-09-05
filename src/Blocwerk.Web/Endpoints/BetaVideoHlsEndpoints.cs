using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// Serves a clip's HLS ladder (master.m3u8 + variant playlists + segments) under
/// <c>/api/beta-videos/{id}/hls/{*path}</c>. Authorization is delegated to
/// <see cref="IBetaVideoService.GetHlsDirectoryAsync"/> — the SAME wall-membership / share-token gate as
/// the progressive byte route — so a denial (or a clip that is not Ready-with-a-ladder) is a 404 and the
/// player falls back to the MP4. The requested sub-path is resolved strictly inside the clip's directory
/// (traversal + symlink guarded), and, on the anonymous share path, playlist URIs are rewritten to carry
/// the token so hls.js's follow-up requests stay authorized.
/// </summary>
public static class BetaVideoHlsEndpoints
{
    private const string PlaylistContentType = "application/vnd.apple.mpegurl";

    public static void MapBetaVideoHls(this WebApplication app)
    {
        app.MapGet("/api/beta-videos/{videoId:guid}/hls/{*path}", HandleAsync);
    }

    private static async Task<IResult> HandleAsync(
        Guid videoId,
        string path,
        [FromQuery] string? token,
        HttpContext http,
        IBetaVideoService betaVideoService,
        IBetaVideoStorage storage)
    {
        // Same access model as GET /api/beta-videos/{id}: the service resolves the caller (cookie member,
        // kiosk cookie, or share token) and rejects API-key principals. Any denial is a 404, never a hint
        // that the clip exists.
        string? directory;
        try
        {
            directory = await betaVideoService.GetHlsDirectoryAsync(videoId, token);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.NotFound();
        }

        if (directory is null)
        {
            return Results.NotFound();
        }

        // Path-traversal + symlink guard: resolve strictly inside the clip's HLS directory and only for
        // the extensions a ladder contains. Anything else (../, absolute, a symlink out of the store) is
        // rejected as not found.
        var file = storage.ResolveHlsFile(videoId, path);
        if (file is null)
        {
            return Results.NotFound();
        }

        return IsPlaylist(file)
            ? await ServePlaylistAsync(http, file, token)
            : ServeSegment(http, file);
    }

    private static async Task<IResult> ServePlaylistAsync(HttpContext http, string file, string? token)
    {
        var content = await File.ReadAllTextAsync(file, http.RequestAborted);

        // Only the token (anonymous share) path needs rewriting; cookie viewers send their cookie on the
        // child requests automatically. A rewritten playlist embeds the token, so it must not be cached.
        if (!string.IsNullOrEmpty(token))
        {
            content = HlsPlaylistRewriter.AppendToken(content, token);
            http.Response.Headers.CacheControl = "private, no-store";
        }
        else
        {
            http.Response.Headers.CacheControl = "private, max-age=5";
        }

        return Results.Text(content, PlaylistContentType);
    }

    private static IResult ServeSegment(HttpContext http, string file)
    {
        // Segment names are deterministic (v%v_%03d.ts), so a re-encode reuses the same URLs. Keep the
        // cache short (a few minutes) so a client's private cache cannot serve stale segment bytes against
        // a freshly re-encoded playlist for up to an hour. Still long enough to cover a single playback.
        http.Response.Headers.CacheControl = "private, max-age=300";
        return Results.File(file, ContentTypeFor(file), enableRangeProcessing: true);
    }

    private static bool IsPlaylist(string file) =>
        file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static string ContentTypeFor(string file) =>
        file.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ? "video/mp4" : "video/mp2t";
}
