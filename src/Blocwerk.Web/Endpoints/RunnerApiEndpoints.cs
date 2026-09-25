// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

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
/// runner's own claim (a 404 otherwise, a 410 once it was taken away).
/// </summary>
public static partial class RunnerApiEndpoints
{
    public const string Prefix = "/api/runners";
    public const string RateLimitPolicy = "runners";

    /// <summary>Seconds a throttled runner is told to wait (<c>Retry-After</c>).</summary>
    public const int RetryAfterSeconds = 10;

    private const string BearerPrefix = "Bearer ";

    public static void MapRunnerApi(this WebApplication app)
    {
        var group = app.MapGroup(Prefix).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(RateLimitPolicy);
        group.MapPost("/hello", HelloAsync);
        group.MapPost("/claim", ClaimAsync);
        group.MapGet("/jobs/{jobId:guid}/bundle", BundleAsync);
        group.MapPost("/jobs/{jobId:guid}/progress", ProgressAsync);
        group.MapPut("/jobs/{jobId:guid}/result", ResultAsync);
        group.MapPost("/jobs/{jobId:guid}/fail", FailAsync);
    }

    /// <summary>
    /// Per caller: a token bucket keyed by the key's public prefix and the address, so one runner
    /// (or one guesser) cannot flood the queue. Generous for a well-behaved runner: a claim every
    /// 25 s and a progress report every few seconds. Registered through <see cref="RateLimitPolicies"/>, which
    /// shares the app's single <c>OnRejected</c> with the API-key login's policy.
    /// </summary>
    public static void AddRunnerRateLimit(this IServiceCollection services) =>
        services.AddRateLimitPolicy(
            RateLimitPolicy,
            http => RateLimitPartition.GetTokenBucketLimiter(
                PartitionKey(http),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 60,
                    TokensPerPeriod = 20,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }),
            (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            });

    private static string PartitionKey(HttpContext http)
    {
        var token = Bearer(http);
        var prefix = token is { Length: >= 12 } ? token[..12] : "none";
        return $"{http.Connection.RemoteIpAddress}|{prefix}";
    }

    private static string? Bearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.FirstOrDefault();
        return header is not null && header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }

    /// <summary>The calling runner, or null (and a warning line for the audit trail).</summary>
    private static async Task<GpuRunner?> RunnerAsync(HttpContext http, GpuJobQueue queue, ILogger logger)
    {
        var runner = await queue.AuthenticateAsync(Bearer(http), http.RequestAborted);
        if (runner is null)
        {
            logger.LogWarning(
                "Runner API {Method} {Path}: key refused from {Address}",
                http.Request.Method, http.Request.Path.Value, http.Connection.RemoteIpAddress);
        }

        return runner;
    }

    private static IResult Outcome(RunnerJobOutcome outcome) => outcome switch
    {
        RunnerJobOutcome.Ok => Results.Ok(new { ok = true }),
        RunnerJobOutcome.Gone => Results.Problem("This job is no longer yours (cancelled or requeued).", statusCode: StatusCodes.Status410Gone),
        RunnerJobOutcome.TooLarge => Results.Problem("The trained splat is too large.", statusCode: StatusCodes.Status413PayloadTooLarge),
        RunnerJobOutcome.Invalid => Results.Problem("The upload is not a .ply or .spz splat.", statusCode: StatusCodes.Status422UnprocessableEntity),
        RunnerJobOutcome.UnsupportedEncoding => Results.Problem(
            "Only Content-Encoding: gzip (or none) is accepted.", statusCode: StatusCodes.Status415UnsupportedMediaType),
        _ => Results.NotFound(),
    };
}
