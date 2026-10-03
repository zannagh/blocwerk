// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The runners whose training of a job failed (<see cref="GpuJob.FailedRunnerIdsJson"/>): a small JSON array of runner
/// ids, oldest first, capped at <see cref="MaxRunners"/>. Claiming prefers other runners for such a job.
/// </summary>
public static class GpuJobFailedRunners
{
    /// <summary>The most runner ids one job remembers (the oldest go first).</summary>
    public const int MaxRunners = 16;

    /// <summary>The ids in <paramref name="json"/>; empty when it is null or unreadable.</summary>
    public static IReadOnlyList<Guid> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="runnerId"/> failed the job.</summary>
    public static bool Contains(GpuJob job, Guid runnerId) => Parse(job.FailedRunnerIdsJson).Contains(runnerId);

    /// <summary><paramref name="json"/> with <paramref name="runnerId"/> added (once; the newest last).</summary>
    public static string With(string? json, Guid runnerId)
    {
        var ids = Parse(json).Where(id => id != runnerId).Append(runnerId).ToList();
        return JsonSerializer.Serialize(ids.Skip(Math.Max(0, ids.Count - MaxRunners)));
    }
}
