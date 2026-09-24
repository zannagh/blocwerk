// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>The handlers of <see cref="RunnerApiEndpoints"/>.</summary>
public static partial class RunnerApiEndpoints
{
    private const string StatsHeader = "X-Blocwerk-Stats";

    private static async Task<IResult> HelloAsync(
        HttpContext http, [FromBody] RunnerHello hello, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        if (await RunnerAsync(http, queue, logger) is not { } runner)
        {
            return Results.Unauthorized();
        }

        await queue.HelloAsync(runner, hello, http.RequestAborted);
        return Results.Ok(new { runnerId = runner.Id, name = runner.Name, pollSeconds = (int)queue.Options.ClaimWait.TotalSeconds });
    }

    private static async Task<IResult> ClaimAsync(HttpContext http, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        if (await RunnerAsync(http, queue, logger) is not { } runner)
        {
            return Results.Unauthorized();
        }

        try
        {
            var claim = await queue.ClaimAsync(runner, queue.Options.ClaimWait, http.RequestAborted);
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
        if (await RunnerAsync(http, queue, logger) is not { } runner)
        {
            return Results.Unauthorized();
        }

        var (outcome, job) = await queue.FindClaimedAsync(runner, jobId, http.RequestAborted);
        if (outcome != RunnerJobOutcome.Ok || job is null)
        {
            return Outcome(outcome);
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
        if (await RunnerAsync(http, queue, logger) is not { } runner)
        {
            return Results.Unauthorized();
        }

        var outcome = await queue.ProgressAsync(runner, jobId, report, http.RequestAborted);
        return outcome == RunnerJobOutcome.Ok
            ? Results.Ok(new { cancel = false, leaseSeconds = (int)queue.Options.Lease.TotalSeconds })
            : Outcome(outcome);
    }

    private static async Task<IResult> FailAsync(
        Guid jobId, HttpContext http, [FromBody] RunnerFailure failure, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        if (await RunnerAsync(http, queue, logger) is not { } runner)
        {
            return Results.Unauthorized();
        }

        return Outcome(await queue.FailAsync(runner, jobId, failure, http.RequestAborted));
    }

    private static async Task<IResult> ResultAsync(
        Guid jobId, HttpContext http, [FromServices] GpuJobQueue queue, [FromServices] ILogger<GpuJobQueue> logger)
    {
        if (await RunnerAsync(http, queue, logger) is not { } runner)
        {
            return Results.Unauthorized();
        }

        var max = queue.Options.MaxResultBytes;
        if (http.Request.ContentLength > max)
        {
            return Outcome(RunnerJobOutcome.TooLarge);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            // The queue enforces the exact cap while streaming; this only lets such a body in.
            size.MaxRequestBodySize = max + 1;
        }

        var stats = http.Request.Headers[StatsHeader].FirstOrDefault();
        var outcome = await queue.AcceptResultAsync(runner, jobId, http.Request.Body, stats, http.RequestAborted);
        return Outcome(outcome);
    }
}
