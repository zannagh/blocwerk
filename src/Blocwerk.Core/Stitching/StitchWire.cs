using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Stitching;

/// <summary>Translates between the sidecar's lowercase wire strings and our enums.</summary>
public static class StitchWire
{
    /// <summary>Wire spelling of a projection: <c>"angled"</c> or <c>"ortho"</c>.</summary>
    public static string ToWire(this WallPhotoProjection projection) =>
        projection == WallPhotoProjection.Ortho ? "ortho" : "angled";

    /// <summary>Parses a sidecar status string; anything unrecognised counts as still running.</summary>
    public static WallStitchJobStatus ParseStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "queued" => WallStitchJobStatus.Queued,
        "running" => WallStitchJobStatus.Running,
        "succeeded" => WallStitchJobStatus.Succeeded,
        "failed" => WallStitchJobStatus.Failed,
        "cancelled" or "canceled" => WallStitchJobStatus.Cancelled,
        _ => WallStitchJobStatus.Running,
    };

    /// <summary>True once a job can no longer change state.</summary>
    public static bool IsTerminal(this WallStitchJobStatus status) =>
        status is WallStitchJobStatus.Succeeded or WallStitchJobStatus.Failed or WallStitchJobStatus.Cancelled;
}
