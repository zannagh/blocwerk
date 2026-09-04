namespace Blocwerk.Core.Enums;

/// <summary>
/// Where a beta clip is in the normalize-to-web-safe pipeline. Every clip is driven to
/// <see cref="Ready"/> (a universally playable H.264/AAC MP4) by the background normalizer.
/// </summary>
public enum BetaVideoEncodingStatus
{
    /// <summary>Uploaded (or queued by an admin re-encode) and waiting for the normalizer to pick it up.</summary>
    Pending = 0,

    /// <summary>The normalizer is currently probing/remuxing/transcoding this clip.</summary>
    Processing = 1,

    /// <summary>A web-safe rendition is on disk and the player may show a &lt;video&gt;.</summary>
    Ready = 2,

    /// <summary>ffmpeg could not produce a web-safe rendition; see the stored error. The original file is kept.</summary>
    Failed = 3,
}
