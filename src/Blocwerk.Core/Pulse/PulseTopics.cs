// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Pulse;

/// <summary>The <see cref="WallPulse.Topic"/> values: which kind of job moved. A page listens to the topics it shows.</summary>
public static class PulseTopics
{
    /// <summary>A capture's pipeline (<c>WallCapture</c>): stage, progress, textures, follow-ups.</summary>
    public const string Capture = "capture";

    /// <summary>A job on a 3D runner (<c>GpuJob</c>): claimed, heartbeat progress, released, finished.</summary>
    public const string GpuJob = "gpu";

    /// <summary>An "Enhance this wall" run (<c>WallEnhancementRun</c>), and its long step's photo count.</summary>
    public const string Enhancement = "enhancement";

    /// <summary>A panel and 3D refresh (<c>WallRefresh</c>): sorting, running, applying, the summary.</summary>
    public const string Refresh = "refresh";

    /// <summary>A big-wall update session (<c>WallUpdateSession</c>), its shape recognition included.</summary>
    public const string Update = "update";

    /// <summary>"Find holds from all photos" (<c>HoldSearchJobs</c>, in memory).</summary>
    public const string HoldSearch = "holdSearch";
}
