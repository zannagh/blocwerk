namespace Blocwerk.Core.Services;

/// <summary>How confident the sidecar's matcher was about a transferred hold.</summary>
internal enum StitchHoldClass
{
    /// <summary>Found on the new image; the clone is trusted and needs no review.</summary>
    Matched,

    /// <summary>Found, but weakly; the clone is flagged for review.</summary>
    Uncertain,

    /// <summary>Not found; the clone is created at its predicted position and flagged for review.</summary>
    Missing,
}
