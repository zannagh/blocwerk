// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using System.Text.Json;
using Blocwerk.Contracts.Assets;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// Turns the collected entries into a <see cref="WallManifest"/>: a stable order, one entry per distinct sha, the tier
/// totals and the wall hash. The same inputs always give the same bytes, so the wall hash moves only when the listed
/// assets do.
/// </summary>
public static class ManifestAssembly
{
    private static readonly string[] TierOrder = [ManifestTiers.Core, ManifestTiers.Lazy, ManifestTiers.View3d];

    /// <summary>The manifest of <paramref name="entries"/>, with its wall hash and without a cursor.</summary>
    public static WallManifest Assemble(Guid wallId, int generation, IEnumerable<ManifestAsset> entries)
    {
        var assets = Distinct(Order(entries));
        var manifest = new WallManifest(
            ManifestSchema.Version,
            wallId,
            Sha: null,
            Cursor: null,
            generation,
            assets.Count(a => a.Sha is null),
            Totals(assets),
            assets);
        return manifest with { Sha = WallHash(manifest) };
    }

    /// <summary>
    /// The wall hash: SHA-256 of the canonical JSON (<see cref="ManifestSchema.Options"/>) of the manifest without its sha
    /// and cursor, so a feed event that changes no asset leaves it, and the ETag, as they were.
    /// </summary>
    public static string WallHash(WallManifest manifest)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest with { Sha = null, Cursor = null }, ManifestSchema.Options);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private static IEnumerable<ManifestAsset> Order(IEnumerable<ManifestAsset> entries) => entries
        .OrderBy(a => Array.IndexOf(TierOrder, a.Tier))
        .ThenBy(a => a.Kind, StringComparer.Ordinal)
        .ThenBy(a => a.Row)
        .ThenBy(a => a.Col)
        .ThenByDescending(a => a.Gen)
        .ThenBy(a => a.Facet, StringComparer.Ordinal)
        .ThenBy(a => a.Splats)
        .ThenBy(a => a.Id)
        .ThenBy(a => a.Sha, StringComparer.Ordinal);

    // The first entry of a sha wins, which by the order above is its strongest tier: the wall photo that a promote copied
    // from the centre panel is the panel, and a carried photo is its current generation.
    private static List<ManifestAsset> Distinct(IEnumerable<ManifestAsset> ordered)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ordered.Where(a => a.Sha is null || seen.Add(a.Sha)).ToList();
    }

    private static ManifestTotals Totals(IReadOnlyList<ManifestAsset> assets)
    {
        long Sum(string tier) => assets.Where(a => a.Tier == tier).Sum(a => a.Bytes);
        return new ManifestTotals(Sum(ManifestTiers.Core), Sum(ManifestTiers.Lazy), Sum(ManifestTiers.View3d));
    }
}
