// <copyright file="TextureSetStamp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which texture render a model's facet textures are: <paramref name="Key"/> identifies the set by its stored image files
/// (each render saves new files; a corrected model version shares its parent's files, so it keeps the key), and
/// <paramref name="RenderedAt"/> is when its newest row was made. "Render wall textures again" keeps the model id but gives
/// a new key, so a hold placement run on the model is only on these textures when it recorded this key
/// (<see cref="HoldPlacementRun.TextureSetKey"/>).
/// </summary>
/// <param name="Key">The set's key.</param>
/// <param name="RenderedAt">The newest texture row's creation time.</param>
internal sealed record TextureSetStamp(string Key, DateTimeOffset RenderedAt)
{
    /// <summary>The current texture set of <paramref name="modelId"/>, or null when it has no textures.</summary>
    /// <param name="db">The context.</param>
    /// <param name="modelId">The model.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stamp.</returns>
    public static async Task<TextureSetStamp?> OfModelAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        // Few rows per model; the newest is found in memory because SQLite cannot MAX a DateTimeOffset.
        var rows = await db.WallGeometryTextures.AsNoTracking()
            .Where(t => t.GeometryModelId == modelId)
            .Select(t => new { t.FacetId, t.StoredPath, t.CreatedAt })
            .ToListAsync(ct);
        return rows.Count == 0
            ? null
            : new TextureSetStamp(KeyOf(rows.Select(r => (r.FacetId, r.StoredPath))), rows.Max(r => r.CreatedAt));
    }

    /// <summary>The key of a texture set: a hash of its facets' stored image files, independent of their order.</summary>
    /// <param name="textures">Each texture's facet id and stored image name.</param>
    /// <returns>32 hex characters.</returns>
    public static string KeyOf(IEnumerable<(string FacetId, string StoredPath)> textures)
    {
        var lines = textures.Select(t => $"{t.FacetId}\n{t.StoredPath}").Order(StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n\n", lines)));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Whether a run on this stamp's model was placed against these textures: it recorded this key, or (a run from before the
    /// key was recorded) it was made after the newest texture, so a model whose textures were not rendered again since keeps
    /// its placements settled while one rendered again after the run does not.
    /// </summary>
    /// <param name="runKey">The run's <see cref="HoldPlacementRun.TextureSetKey"/>.</param>
    /// <param name="runCreatedAt">The run's creation time.</param>
    /// <returns>True when the run's placements are on these textures.</returns>
    public bool Covers(string? runKey, DateTimeOffset runCreatedAt) =>
        runKey is null ? runCreatedAt >= RenderedAt : string.Equals(runKey, Key, StringComparison.Ordinal);
}
