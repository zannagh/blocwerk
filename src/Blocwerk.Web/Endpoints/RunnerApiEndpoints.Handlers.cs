// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using MinDataRate = Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate;

namespace Blocwerk.Web.Endpoints;

/// <summary>The handlers of <see cref="RunnerApiEndpoints"/>.</summary>
public static partial class RunnerApiEndpoints
{
    private const string StatsHeader = "X-Blocwerk-Stats";

    /// <summary>
    /// Slowest result body accepted, after a grace period (Kestrel's default is 240 B/s: days for 2 GB, all the while
    /// holding an upload slot, a disk file and the deploy gate). As for the capture video upload.
    /// </summary>
    private static readonly MinDataRate MinBodyRate = new(bytesPerSecond: 16 * 1024, gracePeriod: TimeSpan.FromSeconds(30));

    private static async Task<IResult> HelloAsync(
        HttpContext http, [FromBody] RunnerHello hello, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        await queue.HelloAsync(runner, hello, http.RequestAborted);
        return Results.Ok(new { runnerId = runner.Id, name = runner.Name, pollSeconds = (int)queue.Options.ClaimWait.TotalSeconds });
    }

    private static async Task<IResult> ClaimAsync(
        HttpContext http, [FromBody] RunnerClaimRequest? request, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        try
        {
            var claim = await queue.ClaimAsync(runner, queue.Options.ClaimWait, request?.MaxQuality, http.RequestAborted);
            return claim is null ? Results.NoContent() : Results.Ok(claim);
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.NoContent();
        }
    }

    private static async Task<IResult> BundleAsync(
        Guid jobId, HttpContext http, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        var (outcome, job) = await queue.FindClaimedAsync(runner, jobId, http.RequestAborted);
        if (outcome != RunnerJobOutcome.Ok || job is null)
        {
            return Outcome(http, outcome);
        }

        var path = queue.BundlePath(job);
        if (path is null || !File.Exists(path))
        {
            return Results.NotFound();
        }

        logger.LogInformation("Runner {RunnerId} ({Name}) downloads the bundle of GPU job {JobId}", runner.Id, runner.Name, jobId);
        return Results.File(path, "application/zip", $"job-{jobId:N}.zip", enableRangeProcessing: true);
    }

    private static async Task<IResult> ProgressAsync(
        Guid jobId, HttpContext http, [FromBody] RunnerProgress report, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        var outcome = await queue.ProgressAsync(runner, jobId, report, http.RequestAborted);
        return outcome == RunnerJobOutcome.Ok
            ? Results.Ok(new { cancel = false, leaseSeconds = (int)queue.Options.Lease.TotalSeconds })
            : Outcome(http, outcome);
    }

    private static async Task<IResult> FailAsync(
        Guid jobId, HttpContext http, [FromBody] RunnerFailure failure, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        return Outcome(http, await queue.FailAsync(runner, jobId, failure, http.RequestAborted));
    }

    private static async Task<IResult> ResultAsync(
        Guid jobId, HttpContext http, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        var max = queue.Options.MaxResultBytes;
        if (http.Request.ContentLength > max)
        {
            return Outcome(http, RunnerJobOutcome.TooLarge);
        }

        PrepareBody(http, max);

        // Content-Encoding: gzip is decoded while streaming (the cap counts decoded bytes); see GpuJobQueue.Result.
        var stats = http.Request.Headers[StatsHeader].FirstOrDefault();
        var encoding = http.Request.Headers.ContentEncoding.FirstOrDefault();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(queue.Options.MaxUploadDuration);
        try
        {
            return Outcome(http, await queue.AcceptResultAsync(runner, jobId, http.Request.Body, encoding, stats, deadline.Token));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Runner {RunnerId} upload for GPU job {JobId} took longer than {Max}", runner.Id, jobId, queue.Options.MaxUploadDuration);
            return Results.Problem("The upload took too long.", statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (BadHttpRequestException ex)
        {
            // Kestrel's minimum data rate (or a client that hung up) ended the body.
            logger.LogInformation(ex, "Runner {RunnerId} upload for GPU job {JobId} aborted", runner.Id, jobId);
            return Results.Problem("The upload was interrupted.", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Lets a body up to the cap in (the queue enforces the exact cap), and demands a minimum rate on HTTP/1.x.</summary>
    private static void PrepareBody(HttpContext http, long max)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = max + 1;
        }

        // Per-request rates are HTTP/1.x only (Kestrel throws for HTTP/2; Caddy proxies over HTTP/1.1).
        if (HttpProtocol.IsHttp11(http.Request.Protocol) && http.Features.Get<IHttpMinRequestBodyDataRateFeature>() is { } rate)
        {
            rate.MinDataRate = MinBodyRate;
        }
    }
}
