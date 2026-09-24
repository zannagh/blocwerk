// <copyright file="DeployBusyWork.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>Long-running server work that must not be cut off by a deploy (see <see cref="IDeployBusyGate"/>).</summary>
public enum DeployBusyWork
{
    /// <summary>A capture's walk-along video is being streamed to disk. Not resumable: a restart loses the upload.</summary>
    CaptureVideoUpload,

    /// <summary>ffmpeg is turning a capture's video into frames for the photo-real view.</summary>
    CaptureVideoFrames,

    /// <summary>A 3D runner is uploading a trained splat. Not resumable: a restart makes the runner upload again.</summary>
    RunnerResultUpload,
}
