using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blocwerk.Web.HealthChecks;

/// <summary>
/// Writes the <c>/health</c> report as JSON, listing the overall status plus each entry's
/// name/status/description/data, so the "busy" entry (and its details) is visible to a caller.
/// The status code is set by the health middleware from the overall status (Unhealthy => 503),
/// so a busy(Degraded) report stays 200 while a DB-down(Unhealthy) report is 503.
/// </summary>
public static class HealthCheckResponseWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            entries = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data,
            }),
        };

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        return context.Response.WriteAsync(json);
    }
}
