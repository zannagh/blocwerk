// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The recognition side of <see cref="RunnerApiEndpoints"/> (runner protocol 1.2): a recognition job's input envelope and
/// result zip (<see cref="RecognitionResultZip"/>), and the model files a <c>detect-holds@2</c> request names by hash.
/// </summary>
public static partial class RunnerApiEndpoints
{
    private static async Task<IResult> RecognitionBundleAsync(GpuRunner runner, Guid jobId, HttpContext http, RecognitionQueue recognition, ILogger logger)
    {
        // A missing input fails the job at once (410 "over"), see RecognitionQueue.InputForRunnerAsync.
        var (outcome, path) = await recognition.InputForRunnerAsync(runner, jobId, http.RequestAborted);
        if (outcome != RunnerJobOutcome.Ok || path is null)
        {
            return Outcome(http, outcome);
        }

        logger.LogInformation("Runner {RunnerId} ({Name}) downloads the input of recognition job {JobId}", runner.Id, runner.Name, jobId);
        return Results.File(path, "application/zip", $"job-{jobId:N}.zip", enableRangeProcessing: true);
    }

    private static async Task<IResult> RecognitionResultAsync(
        GpuRunner runner, Guid jobId, HttpContext http, GpuJobQueue queue, RecognitionQueue recognition, ILogger logger)
    {
        if (http.Request.ContentLength > RecognitionResultZip.MaxBytes)
        {
            return Results.Problem("The result is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        PrepareBody(http, RecognitionResultZip.MaxBytes);
        var encoding = http.Request.Headers.ContentEncoding.FirstOrDefault();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(queue.Options.MaxUploadDuration);
        try
        {
            var outcome = await recognition.AcceptResultAsync(runner, jobId, http.Request.Body, encoding, deadline.Token);
            return outcome switch
            {
                RunnerJobOutcome.TooLarge => Results.Problem("The result is too large.", statusCode: StatusCodes.Status413PayloadTooLarge),
                RunnerJobOutcome.Invalid => Results.Problem(
                    "The upload is not a recognition result (a zip of only status.json, status succeeded, with an inline result object).",
                    statusCode: StatusCodes.Status422UnprocessableEntity),
                _ => Outcome(http, outcome),
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            return Results.Problem("The upload took too long.", statusCode: StatusCodes.Status408RequestTimeout);
        }
        catch (BadHttpRequestException ex)
        {
            logger.LogInformation(ex, "Runner {RunnerId} result upload for recognition job {JobId} aborted", runner.Id, jobId);
            return Results.Problem("The upload was interrupted.", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary><c>GET /api/runners/models/{sha256}</c>: a model file by hash, with Range. Any runner key may fetch it.</summary>
    private static async Task<IResult> ModelAsync(
        string sha256, HttpContext http, [FromServices] GpuJobQueue queue, [FromServices] RecognitionModelCatalog models, [FromServices] ILogger<GpuJobQueue> logger)
    {
        var (runner, refused) = await RunnerAsync(http, queue, logger);
        if (runner is null)
        {
            return refused;
        }

        if (await models.FindAsync(sha256) is not { } path)
        {
            return Results.NotFound();
        }

        logger.LogInformation("Runner {RunnerId} ({Name}) downloads model {Sha256}", runner.Id, runner.Name, sha256);
        return Results.File(path, "application/octet-stream", $"{sha256}.onnx", enableRangeProcessing: true);
    }
}
