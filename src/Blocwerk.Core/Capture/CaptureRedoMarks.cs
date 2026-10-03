// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Finished captures whose model is being redone in the background: solved again (<see cref="CaptureResolveMark"/>) or
/// its wall textures rendered again (<see cref="CaptureTextureOutcome.RerenderMark"/>). While one runs, the wall's
/// active model must not be replaced by a correction or a new capture: the re-solve registers to (and shares the view
/// of) the model it started from, and the re-render's texture files may be shared with a correction's copy.
/// </summary>
internal static class CaptureRedoMarks
{
    /// <summary>What the admin is told when a correction or a new capture has to wait for one.</summary>
    public const string BusyMessage = "This wall's 3D model is being solved again or its wall textures are being rendered again.";

    /// <summary>The captures of <paramref name="captures"/> that carry a re-solve or re-render mark (translates to SQL).</summary>
    public static IQueryable<WallCapture> Redoing(IQueryable<WallCapture> captures) => captures.Where(c =>
        (c.SolveJobId != null && c.SolveJobId.StartsWith(CaptureResolveMark.Mark))
        || (c.TexturesJobId != null && c.TexturesJobId.StartsWith(CaptureTextureOutcome.RerenderMark)));
}
