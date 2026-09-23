// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Capture;

/// <summary>One stored level of a splat scene's level-of-detail ladder (a pruned <c>.spz</c>).</summary>
/// <param name="Splats">Splat count of the level.</param>
/// <param name="StoredPath">Stored name under the capture file store (a bare file name).</param>
/// <param name="SizeBytes">Byte size of the file.</param>
public sealed record SplatLodLevel(int Splats, string StoredPath, long SizeBytes);

/// <summary>
/// The level-of-detail ladder of a photo-real scene (<see cref="WallGeometrySplat.LodLevelsJson"/>):
/// the scene pruned by <see cref="SpzDecimator"/> to a few sizes, all smaller than the full one. The 3D
/// view starts a device on the smallest level (it shows within a second, even on a phone) and steps
/// up one level at a time while frames stay fast, so a phone never has to survive the full scene.
/// </summary>
public static class SplatLodLadder
{
    /// <summary>A level is only built when it is at most this fraction of the full scene.</summary>
    public const double MaxFraction = 0.8;

    /// <summary>
    /// Splat counts of the ladder's levels. The full scene is the implicit top. 800k is a desktop step
    /// between a phone's 250k cap and a <see cref="SplatQuality.High"/> scene's 1–2 M; it is only built
    /// for scenes of at least 1 M (<see cref="MaxFraction"/>).
    /// </summary>
    public static readonly IReadOnlyList<int> Targets = [40_000, 120_000, 250_000, 800_000];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The pruned levels of <paramref name="spz"/>, ascending; empty for a small scene.</summary>
    public static IReadOnlyList<(int Splats, byte[] Spz)> Build(byte[] spz) =>
        SpzDecimator.Ladder(spz, Targets, MaxFraction);

    /// <summary>The stored levels, ascending by splat count; empty for null or unreadable JSON.</summary>
    public static IReadOnlyList<SplatLodLevel> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<SplatLodLevel>>(json, Json) ?? [])
                .Where(l => l.Splats > 0 && !string.IsNullOrEmpty(l.StoredPath))
                .OrderBy(l => l.Splats)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IEnumerable<SplatLodLevel> levels) =>
        JsonSerializer.Serialize(levels.OrderBy(l => l.Splats).ToList(), Json);

    /// <summary>Every stored file of a splat row: the full scene, the legacy mobile copy and the ladder.</summary>
    public static IEnumerable<string?> Files(WallGeometrySplat splat) =>
        new[] { splat.StoredPath, splat.MobileStoredPath }.Concat(Parse(splat.LodLevelsJson).Select(l => l.StoredPath));
}
