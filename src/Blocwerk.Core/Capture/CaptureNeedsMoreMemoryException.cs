// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>
/// A compute job stopped because it needed more memory than the host's worker allows one job (<c>failureKind: resource</c>).
/// Not a failure of the capture: where a 3D runner can take the work it is deferred to one (textures), else the step waits
/// and runs again on the next attempt (solve). Callers that do not handle it treat it as the <see cref="CaptureFailedException"/> it is.
/// </summary>
public sealed class CaptureNeedsMoreMemoryException(string message) : CaptureFailedException(message)
{
    /// <summary>What the capture shows while the work waits.</summary>
    public const string WaitingText = "will be done once a runner becomes available";
}
