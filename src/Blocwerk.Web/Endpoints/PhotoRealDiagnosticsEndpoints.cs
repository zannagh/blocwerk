// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// What a device reports about the photo-real (Gaussian splat) 3D view: start, level steps, lost WebGL
/// contexts, failures. The endpoint only logs it, structured, so a phone's real limits can be read
/// from the server log (no browser console on an iPhone). Signed-in browsers only; nothing personal
/// (no user agent, no ids beyond what the log scope already carries); rate-limited per user.
/// </summary>
public static class PhotoRealDiagnosticsEndpoints
{
    public const string Route = "/api/diagnostics/photo-real";

    /// <summary>Reports per user per <see cref="Window"/>; more are dropped with 429.</summary>
    public const int MaxReports = 60;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> Recent = new();

    public static void MapPhotoRealDiagnostics(this WebApplication app)
    {
        app.MapPost(Route, Handle)
            .RequireAuthorization()
            .DenyApiKeyPrincipals()
            .DisableAntiforgery();
    }

    internal static IResult Handle([FromBody] PhotoRealReport report, ClaimsPrincipal user, ILoggerFactory loggerFactory)
    {
        var key = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity?.Name ?? "?";
        if (!Admit(key, DateTimeOffset.UtcNow))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        loggerFactory.CreateLogger("Blocwerk.PhotoReal").LogInformation(
            "Photo-real {Event}: level {Level}/{Levels} ({Splats} splats), renderer {Renderer} ({Vendor}), "
            + "maxTexture {MaxTexture}, deviceMemory {DeviceMemory}, dpr {Dpr} (render ratio {PixelRatio}), "
            + "canvas {CanvasWidth}x{CanvasHeight}, frame {FrameMs} ms, after {ElapsedMs} ms, lost {LostCount}, "
            + "safe cap {SafeSplats}, mobile {Mobile}, detail {Detail}",
            Clip(report.Event, 32), report.Level, report.Levels, report.Splats, Clip(report.Renderer, 128),
            Clip(report.Vendor, 64), report.MaxTextureSize, report.DeviceMemory, report.Dpr, report.PixelRatio,
            report.CanvasWidth, report.CanvasHeight, report.FrameMs, report.ElapsedMs, report.LostCount,
            report.SafeSplats, report.Mobile, Clip(report.Detail, 256));
        return Results.NoContent();
    }

    /// <summary>Sliding-window admission; also forgets idle users so the map stays small.</summary>
    internal static bool Admit(string key, DateTimeOffset now)
    {
        var queue = Recent.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxReports)
            {
                return false;
            }

            queue.Enqueue(now);
        }

        if (Recent.Count > 1000)
        {
            foreach (var (k, q) in Recent)
            {
                lock (q)
                {
                    if (q.Count == 0 || now - q.Peek() > Window)
                    {
                        Recent.TryRemove(k, out _);
                    }
                }
            }
        }

        return true;
    }

    private static string? Clip(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
