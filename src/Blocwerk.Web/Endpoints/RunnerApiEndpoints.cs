// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Threading.RateLimiting;
using Blocwerk.Authentication.Endpoints;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The pull API of the 3D runners (<see cref="GpuRunner"/>): a runner connects OUT with its
/// <c>bwr_</c> key, says hello, long-polls for work, downloads its claimed job's bundle, reports
/// progress (the lease heartbeat) and uploads the trained splat. Nothing here authenticates a user:
/// the key names a runner, never a person, and every job-scoped call re-checks that the job is the
/// runner's own claim (a 404 otherwise, a 410 once it was taken away or the runner may no longer train its wall).
/// Not mapped at all with <c>RUNNERS__MODE=off</c>.
/// </summary>
public static partial class RunnerApiEndpoints
{
    public const string Prefix = "/api/runners";
    public const string RateLimitPolicy = "runners";

    /// <summary>Seconds a throttled runner is told to wait (<c>Retry-After</c>).</summary>
    public const int RetryAfterSeconds = 10;

    /// <summary>Calls one address may make in a burst (several runners may share a home NAT).</summary>
    public const int AddressBurst = 120;

    private const string BearerPrefix = "Bearer ";

    public static void MapRunnerApi(this WebApplication app)
    {
        var options = app.Services.GetService<GpuRunnerOptions>() ?? app.Services.GetRequiredService<GpuJobQueue>().Options;
        if (options.Mode == GpuRunnerMode.Off)
        {
            return;
        }

        var group = app.MapGroup(Prefix).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/hello", HelloAsync);
        group.MapPost("/claim", ClaimAsync);
        group.MapGet("/jobs/{jobId:guid}/bundle", BundleAsync);
        group.MapPost("/jobs/{jobId:guid}/progress", ProgressAsync);
        group.MapPut("/jobs/{jobId:guid}/result", ResultAsync);
        group.MapPost("/jobs/{jobId:guid}/fail", FailAsync);
    }

    /// <summary>
    /// Per address: a token bucket keyed by the remote address ONLY. Nothing from the request (such as the key's
    /// prefix) goes into the key, because before authentication all of it is the caller's to rotate. Once the key
    /// authenticated, <see cref="GpuJobQueue.Calls"/> limits each runner on its own. Registered through
    /// <see cref="RateLimitPolicies"/>, which shares the app's single <c>OnRejected</c> with the API-key login's policy.
    /// </summary>
    public static void AddRunnerRateLimit(this IServiceCollection services) =>
        services.AddRateLimitPolicy(
            RateLimitPolicy,
            http => RateLimitPartition.GetTokenBucketLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = AddressBurst,
                    TokensPerPeriod = 40,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }),
            (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            });

    private static string? Bearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault();
        return header is not null && header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }

    /// <summary>The calling runner, or the answer to give instead: 401 (and a warning line for the audit trail) or 429.</summary>
    private static async Task<(GpuRunner? Runner, IResult Refused)> RunnerAsync(HttpContext http, GpuJobQueue queue, ILogger logger)
    {
        var runner = await queue.AuthenticateAsync(Bearer(http), http.RequestAborted);
        if (runner is null)
        {
            logger.LogWarning(
                "Runner API {Method} {Path}: key refused from {Address}",
                http.Request.Method, http.Request.Path.Value, http.Connection.RemoteIpAddress);
            return (null, Results.Unauthorized());
        }

        if (!queue.Calls.TryAcquire(runner.Id))
        {
            return (null, TooManyRequests(http, "This runner calls too often."));
        }

        return (runner, Results.Empty);
    }

    private static IResult TooManyRequests(HttpContext http, string detail)
    {
        http.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return Results.Problem(detail, statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static IResult Outcome(HttpContext http, RunnerJobOutcome outcome) => outcome switch
    {
        RunnerJobOutcome.Ok => Results.Ok(new { ok = true }),
        RunnerJobOutcome.Gone => Results.Problem("This job is no longer yours (cancelled or requeued).", statusCode: StatusCodes.Status410Gone),
        RunnerJobOutcome.TooLarge => Results.Problem("The trained splat is too large.", statusCode: StatusCodes.Status413PayloadTooLarge),
        RunnerJobOutcome.Invalid => Results.Problem("The upload is not a valid .ply or .spz splat.", statusCode: StatusCodes.Status422UnprocessableEntity),
        RunnerJobOutcome.UnsupportedEncoding => Results.Problem(
            "Only Content-Encoding: gzip (or none) is accepted.", statusCode: StatusCodes.Status415UnsupportedMediaType),
        RunnerJobOutcome.UploadInProgress => Results.Problem(
            "Another upload for this job is still running.", statusCode: StatusCodes.Status409Conflict),
        RunnerJobOutcome.ServerBusy => TooManyRequests(http, "The server takes no more uploads right now; retry shortly."),
        RunnerJobOutcome.InsufficientStorage => Results.Problem(
            "The server is short of disk space; retry later.", statusCode: StatusCodes.Status507InsufficientStorage),
        _ => Results.NotFound(),
    };
}
