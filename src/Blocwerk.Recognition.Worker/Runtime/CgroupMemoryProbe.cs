// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// The container's working set as the kubelet counts it: cgroup v2 <c>memory.current</c> (v1 <c>memory.usage_in_bytes</c>)
/// minus the inactive file cache, so native allocations (OpenCV, ONNX Runtime, Skia) count and a model file just read
/// does not. Outside a cgroup: the process working set.
/// </summary>
public sealed class CgroupMemoryProbe : IMemoryProbe
{
    private static readonly (string Usage, string Stat, string Inactive)[] Layouts =
    [
        ("/sys/fs/cgroup/memory.current", "/sys/fs/cgroup/memory.stat", "inactive_file"),
        ("/sys/fs/cgroup/memory/memory.usage_in_bytes", "/sys/fs/cgroup/memory/memory.stat", "total_inactive_file"),
    ];

    private readonly (string Usage, string Stat, string Inactive)? layout = Layouts.FirstOrDefault(l => File.Exists(l.Usage)) is { Usage: not null } l ? l : null;

    public long CurrentBytes()
    {
        if (layout is not { } l || Parse(File.ReadAllText(l.Usage)) is not { } usage)
        {
            return Environment.WorkingSet;
        }

        var inactive = File.Exists(l.Stat)
            ? File.ReadLines(l.Stat).Select(line => line.Split(' ')).Where(f => f.Length == 2 && f[0] == l.Inactive).Select(f => Parse(f[1])).FirstOrDefault()
            : null;
        return Math.Max(0, usage - (inactive ?? 0));
    }

    private static long? Parse(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
