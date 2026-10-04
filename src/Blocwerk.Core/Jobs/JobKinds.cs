// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Jobs;

/// <summary>The <see cref="JobProgressItem.Kind"/> values. Part of the API contract: never rename one.</summary>
public static class JobKinds
{
    /// <summary>A capture's pipeline (queued, detecting, solving, texturing, splatting).</summary>
    public const string Capture = "capture";

    /// <summary>A photo-real training on a 3D runner (queued, download, training, upload).</summary>
    public const string GpuTraining = "gpuTraining";

    /// <summary>The server finishing a trained view (crop, export, LOD) and installing it.</summary>
    public const string Finish = "finish";

    /// <summary>One step of a capture's follow-up chain.</summary>
    public const string FollowUp = "followUp";

    /// <summary>A finished capture's wall textures rendered again.</summary>
    public const string TextureRerender = "textureRerender";

    /// <summary>A finished capture's 3D model solved again.</summary>
    public const string Resolve = "resolve";

    /// <summary>A capture package being imported (replayed) from another instance.</summary>
    public const string Import = "import";

    /// <summary>A wall admin's "Find holds from all photos" search (queued, then one photo after the other).</summary>
    public const string HoldSearch = "holdSearch";

    /// <summary>Every kind, in display order.</summary>
    public static IReadOnlyList<string> All { get; } = [Capture, GpuTraining, Finish, FollowUp, TextureRerender, Resolve, Import, HoldSearch];
}

/// <summary>The <see cref="JobProgressItem.State"/> values. Part of the API contract: never rename one.</summary>
public static class JobStates
{
    public const string Queued = "queued";

    public const string Running = "running";

    public const string Succeeded = "succeeded";

    /// <summary>Ended without doing anything (a follow-up step with nothing to do).</summary>
    public const string Skipped = "skipped";

    public const string Failed = "failed";

    public const string Cancelled = "cancelled";

    /// <summary>Whether the job is still to end.</summary>
    public static bool IsActive(string state) => state is Queued or Running;
}

/// <summary>The <see cref="JobProgressItem.EtaSource"/> values.</summary>
public static class JobEtaSources
{
    /// <summary>From the job's own rate (steps or bytes per second since the current claim started).</summary>
    public const string Rate = "rate";

    /// <summary>From the median duration of the same stage in recent jobs.</summary>
    public const string History = "history";
}
