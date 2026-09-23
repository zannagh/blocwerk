using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture;

/// <summary>
/// A pipeline stage backed by one compute job, mapped onto a slice of the overall progress.
/// <paramref name="Describe"/> turns a running job's status into the progress label (default:
/// "Label: stage").
/// </summary>
internal sealed record JobStage(
    Guid CaptureId,
    WallCaptureStatus Status,
    double From,
    double To,
    string Label,
    Func<ComputeJobStatus, string>? Describe = null);

/// <summary>Polling a compute job with back-off, a job timeout and a transient-error budget.</summary>
public sealed partial class WallCaptureProcessor
{
    private async Task<ComputeJobStatus> PollAsync(
        IComputeJobClient client, string jobId, JobStage stage, CancellationToken ct)
    {
        var timeout = ComputeJobClientFactory.SettingsFor(settings, client.Service).JobTimeout;
        var deadline = DateTimeOffset.UtcNow + timeout;
        var delay = options.PollInitialDelay;
        var transientErrors = 0;
        string? lastLabel = null;
        while (true)
        {
            ComputeJobStatus status;
            try
            {
                status = await client.GetStatusAsync(jobId, ct);
                transientErrors = 0;
            }
            catch (ComputeJobException ex) when (ex.IsTransient && ++transientErrors <= options.MaxTransientErrors)
            {
                status = new ComputeJobStatus { Status = ComputeJobStates.Running, Stage = "waiting for the service" };
            }

            if (status.Status == ComputeJobStates.Succeeded)
            {
                return status;
            }

            if (status.Status is ComputeJobStates.Failed or ComputeJobStates.Cancelled)
            {
                // The worker's text can carry paths and stack frames: logged in full, shown shortened.
                var raw = status.Error ?? status.Message;
                logger.LogWarning("Compute job {JobId} ({Stage}) ended {Status}: {Reason}", jobId, stage.Label, status.Status, raw);
                var reason = ComputeErrorText.Sanitize(raw) ?? status.Status;
                throw new CaptureFailedException($"{stage.Label} failed: {reason}");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                await client.CancelAsync(jobId, CancellationToken.None);
                throw new CaptureFailedException(
                    $"{stage.Label} took longer than {timeout.TotalMinutes:0} minutes and was stopped.");
            }

            var label = stage.Describe?.Invoke(status)
                        ?? (string.IsNullOrWhiteSpace(status.Stage) ? stage.Label : $"{stage.Label}: {status.Stage}");
            if (label != lastLabel || status.Progress is not null)
            {
                var progress = stage.From + ((stage.To - stage.From) * Math.Clamp(status.Progress ?? 0, 0, 1));
                await SetStageAsync(stage.CaptureId, stage.Status, progress, label, ct);
                lastLabel = label;
            }

            await Task.Delay(delay, ct);
            delay = TimeSpan.FromTicks(Math.Min(options.PollMaxDelay.Ticks, (long)(delay.Ticks * 1.5)));
        }
    }
}
