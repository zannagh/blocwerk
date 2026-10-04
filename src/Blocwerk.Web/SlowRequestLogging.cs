// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Diagnostics;

namespace Blocwerk.Web;

/// <summary>Logs an HTTP request that took longer than a second at Warning (method, path without the query, status, time).</summary>
public static class SlowRequestLogging
{
    public static readonly TimeSpan Threshold = TimeSpan.FromSeconds(1);

    public static void UseSlowRequestLogging(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Blocwerk.SlowRequests");
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await next();
            }
            finally
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                if (elapsed >= Threshold && IsMeasurable(context))
                {
                    logger.LogWarning(
                        "Slow request: {Method} {Path} -> {Status} in {ElapsedMs} ms",
                        context.Request.Method,
                        context.Request.Path.Value,
                        context.Response.StatusCode,
                        (long)elapsed.TotalMilliseconds);
                }
            }
        });
    }

    // A circuit's websocket and long polling, and an upload of a few MB over a phone connection, legitimately take long.
    private static bool IsMeasurable(HttpContext context) =>
        !context.WebSockets.IsWebSocketRequest &&
        !context.Request.Path.StartsWithSegments("/_blazor") &&
        !(context.Request.ContentLength > 1_000_000);
}
