// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// What a 3D runner advertises in <c>hello</c> (<c>capabilities</c>) and which jobs that lets it claim. A runner that
/// advertises none (every version before textures jobs) counts as <c>splat</c> only, so it never sees a textures job.
/// </summary>
public static class RunnerCapabilities
{
    /// <summary>Trains photo-real views.</summary>
    public const string Splat = "splat";

    /// <summary>Renders wall textures (the wall-geometry package is importable and memory suffices).</summary>
    public const string Textures = "textures";

    private static readonly string[] Known = [Splat, Textures];

    /// <summary>The stored form of a hello's list: known names only, de-duplicated, comma separated; null when none was sent.</summary>
    public static string? Store(IEnumerable<string>? reported)
    {
        var names = (reported ?? []).Select(n => n?.Trim().ToLowerInvariant()).Where(n => n is not null && Known.Contains(n)).Distinct().ToList();
        return names.Count == 0 ? null : string.Join(',', names);
    }

    /// <summary>Whether a runner with this stored list may claim jobs of <paramref name="kind"/>.</summary>
    public static bool Allows(string? stored, GpuJobKind kind) => kind switch
    {
        GpuJobKind.Textures => Has(stored, Textures),
        _ => stored is null || Has(stored, Splat),
    };

    /// <summary>Whether the stored list names <paramref name="capability"/>.</summary>
    public static bool Has(string? stored, string capability) =>
        stored is not null && stored.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(capability);

    /// <summary>The wire name of a job kind (the claim's <c>kind</c>).</summary>
    public static string KindName(GpuJobKind kind) => kind == GpuJobKind.Textures ? Textures : Splat;
}
