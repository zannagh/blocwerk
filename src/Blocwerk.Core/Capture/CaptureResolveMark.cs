// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture;

/// <summary>
/// A finished capture's 3D model solved again from its photos (<see cref="WallCaptureService.ResolveModelAsync"/>) is
/// marked on <see cref="Entities.WallCapture.SolveJobId"/>: the mark alone while queued, the solve job id after it.
/// </summary>
internal static class CaptureResolveMark
{
    public const string Mark = "resolve:";

    public static bool IsResolving(string? solveJobId) => solveJobId?.StartsWith(Mark, StringComparison.Ordinal) == true;

    /// <summary>The submitted solve job, or null when it is still to be submitted (or none runs).</summary>
    public static string? JobId(string? solveJobId) =>
        IsResolving(solveJobId) && solveJobId!.Length > Mark.Length ? solveJobId[Mark.Length..] : null;
}
