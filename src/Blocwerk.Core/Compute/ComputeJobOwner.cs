// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Compute;

/// <summary>
/// Whose job a submission is: a worker that queues the job for a 3D runner (<c>RunnerSplatClient</c>) needs the wall, the
/// capture and the model (null before the capture has one: a markerless reconstruction); an HTTP worker ignores it.
/// </summary>
public sealed record ComputeJobOwner(Guid WallId, Guid CaptureId, Guid? ModelId);
