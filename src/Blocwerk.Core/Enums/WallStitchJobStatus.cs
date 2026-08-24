namespace Blocwerk.Core.Enums;

/// <summary>Lifecycle of a photo-stitching job handed to the external stitch sidecar.</summary>
public enum WallStitchJobStatus
{
    /// <summary>Accepted by the sidecar but not picked up yet.</summary>
    Queued = 0,

    /// <summary>Being registered, rectified, blended or hold-matched.</summary>
    Running = 1,

    /// <summary>Finished; artifacts and transferred holds are available for download.</summary>
    Succeeded = 2,

    /// <summary>Ended with an error; see the error code and message on the job.</summary>
    Failed = 3,

    /// <summary>Abandoned by the requester before it finished.</summary>
    Cancelled = 4,
}
