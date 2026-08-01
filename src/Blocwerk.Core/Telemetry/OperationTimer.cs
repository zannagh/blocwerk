using System.Diagnostics;

namespace Blocwerk.Core.Telemetry;

/// <summary>
/// Ties together a trace span and a duration measurement for one service operation.
/// Disposing it stops the timer, records <see cref="BlocwerkMetrics.OperationDuration"/> tagged
/// with the operation name (and anonymized wall, when given), and marks the span's status from
/// <see cref="Fail"/>. Created via <see cref="BlocwerkMetrics.TimeOperation"/>.
/// </summary>
public readonly struct OperationTimer : IDisposable
{
    private readonly string operation;
    private readonly Guid? wallId;
    private readonly long startTimestamp;
    private readonly Activity? activity;

    internal OperationTimer(string operation, Guid? wallId)
    {
        this.operation = operation;
        this.wallId = wallId;
        startTimestamp = Stopwatch.GetTimestamp();
        activity = Otel.ActivitySource.StartActivity(operation, ActivityKind.Internal);

        if (activity != null && wallId.HasValue)
        {
            activity.SetTag("wall", BlocwerkMetrics.AnonymizeWallId(wallId.Value));
        }
    }

    /// <summary>
    /// Marks the operation (span) as failed and records the exception on it. The duration is
    /// still recorded on dispose, tagged <c>status=error</c>.
    /// </summary>
    public void Fail(Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        activity?.AddException(ex);
    }

    public void Dispose()
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        var tags = new TagList { { "operation", operation } };
        if (wallId.HasValue)
        {
            tags.Add("wall", BlocwerkMetrics.AnonymizeWallId(wallId.Value));
        }

        if (activity?.Status == ActivityStatusCode.Error)
        {
            tags.Add("status", "error");
        }

        BlocwerkMetrics.OperationDuration.Record(elapsedMs, tags);
        activity?.Dispose();
    }
}
