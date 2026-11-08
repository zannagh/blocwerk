// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.InteropServices;
using Blocwerk.Recognition.Worker.Models;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// The runner loop (protocol §1.2): read the key file when the key comes from one, warm the models, hello, then claim
/// (long-poll) and run one job at a time. A hello whose answer confirms no recognition capability (a 1.1 server) stops the
/// worker with exit code 2 (§1.5); a refused key with 3; both after <see cref="WorkerOptions.ExitBackoff"/>, so a restart
/// loop stays slow. After a transient error the loop says hello again before the next claim.
/// </summary>
public sealed class RunnerLoop(
    RunnerApiClient api,
    JobExecutor executor,
    ModelStore models,
    YoloBoxDetector detector,
    IMemoryProbe memory,
    WorkerOptions options,
    WorkerHealth health,
    WorkerExit exit,
    ILogger<RunnerLoop> logger) : BackgroundService
{
    private const long Mb = 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        await Task.Yield();
        try
        {
            if (options.Key.Length == 0 && options.KeyFile is { } keyFile)
            {
                api.UseKey(await KeyFileWait.ReadAsync(keyFile, options, health, logger, stopping));
            }

            await WarmAsync(stopping);
            while (!stopping.IsCancellationRequested)
            {
                if (!await HelloAsync(stopping))
                {
                    await ExitAsync(WorkerExit.NotConfirmed, stopping);
                    return;
                }

                await ClaimLoopAsync(stopping);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogCritical("{Message} Check RECOGNITION__INSTANCEKEY / RECOGNITION__INSTANCEKEYFILE / RECOGNITION__RUNNERKEY.", ex.Message);
            await ExitAsync(WorkerExit.Unauthorized, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task WarmAsync(CancellationToken ct)
    {
        if (options.WarmModel is { } name && await models.FindLocalAsync(name, ct) is { } model)
        {
            detector.Load(model.Path, model.Sha256);
            logger.LogInformation("Model {Name} ({Sha256}) loaded", name, model.Sha256);
        }

        health.MarkModelsLoaded();
    }

    private async Task<bool> HelloAsync(CancellationToken stopping)
    {
        // Back off up to a minute while the app is down or does not map the runner API (RECOGNITION__MODE=inprocess).
        for (var delay = options.RetryDelay; ; delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromMinutes(1).Ticks)))
        {
            try
            {
                health.MarkAlive();
                var answer = await api.HelloAsync(Hello(), stopping);
                var confirmed = executor.Kinds.Intersect(answer.Capabilities ?? []).ToList();
                if (confirmed.Count == 0)
                {
                    logger.LogCritical("The server at {Server} confirmed no recognition capability (it needs runner protocol 1.2); not starting", options.Server);
                    return false;
                }

                health.MarkHello(confirmed);
                logger.LogInformation("Runner {RunnerId} serves {Kinds}", answer.RunnerId, string.Join(", ", confirmed));
                return true;
            }
            catch (Exception ex) when (IsTransient(ex, stopping))
            {
                logger.LogWarning("Hello to {Server} failed: {Message}", options.Server, ex.Message);
                await Task.Delay(delay, stopping);
            }
        }
    }

    private async Task ClaimLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            RunnerClaim? claim;
            try
            {
                health.MarkAlive();
                using var call = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                call.CancelAfter(options.ClaimTimeout);
                claim = await api.ClaimAsync(call.Token);
                health.MarkContact();
            }
            catch (Exception ex) when (IsTransient(ex, stopping))
            {
                logger.LogWarning("Claim failed: {Message}", ex.Message);
                await Task.Delay(options.RetryDelay, stopping);
                return;
            }

            if (claim is not null)
            {
                await executor.RunAsync(claim, stopping);
            }
        }
    }

    private RunnerHello Hello() => new(
        $"recognition-{typeof(RunnerLoop).Assembly.GetName().Version}",
        $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}",
        MemoryBudgetMb(),
        Paused: false,
        executor.Kinds);

    /// <summary>Design §4: <c>JOB_MAX_MEMORY_MB</c> minus what is resident now (models loaded); without it the host's memory.</summary>
    private long MemoryBudgetMb()
    {
        var ceiling = options.JobMaxMemoryMb > 0 ? options.JobMaxMemoryMb : GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / Mb;
        return Math.Max(1, ceiling - (memory.CurrentBytes() / Mb));
    }

    private static bool IsTransient(Exception ex, CancellationToken stopping) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !stopping.IsCancellationRequested);

    private async Task ExitAsync(int exitCode, CancellationToken stopping)
    {
        logger.LogInformation("Exiting with code {Code} in {Backoff}", exitCode, options.ExitBackoff);
        await Task.Delay(options.ExitBackoff, stopping).ContinueWith(_ => { }, TaskScheduler.Default);
        exit.Request(exitCode);
    }
}
