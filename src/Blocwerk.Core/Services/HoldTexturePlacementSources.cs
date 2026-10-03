// <copyright file="HoldTexturePlacementSources.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>A panel photo as it was read: its generation, in-place revision and byte length, stamped in the same query as the bytes.</summary>
/// <param name="Generation">The panel's photo generation.</param>
/// <param name="Length">The photo's byte length.</param>
/// <param name="Revision">The panel's <see cref="Entities.WallPanel.PhotoRevision"/> (moves on a crop or its undo).</param>
internal readonly record struct PanelPhotoStamp(int Generation, int Length, int Revision = 0);

/// <summary>
/// A panel photo as registered onto a model's textures: a new photo (generation or size), another model or the same model's
/// textures rendered again (<see cref="TextureSetStamp.Key"/>) is another key.
/// </summary>
/// <param name="PanelId">The panel.</param>
/// <param name="Photo">The photo, stamped when its bytes were read.</param>
/// <param name="ModelId">The model registered onto.</param>
/// <param name="TextureSetKey">The model's texture set registered onto.</param>
internal readonly record struct PanelRegistrationKey(Guid PanelId, PanelPhotoStamp Photo, Guid ModelId, string? TextureSetKey);

/// <summary>
/// Panel photo registrations kept in memory (a few KB each), filled by every placement run and by the edited holds'
/// placement, so a user's edit maps its hold through a known registration instead of matching the photo again.
/// Process-wide and bounded; a restart simply registers again. A registration that accepted no facet is never kept.
/// </summary>
internal static class PanelRegistrationCache
{
    private const int MaxEntries = 256;

    private static readonly ConcurrentDictionary<PanelRegistrationKey, IReadOnlyList<FacetRegistration>> Entries = new();

    public static IReadOnlyList<FacetRegistration>? Get(PanelRegistrationKey key) => Entries.GetValueOrDefault(key);

    public static void Put(PanelRegistrationKey key, IReadOnlyList<FacetRegistration> registrations)
    {
        if (!registrations.Any(r => r.Accepted))
        {
            return;
        }

        if (Entries.Count >= MaxEntries)
        {
            Entries.Clear();
        }

        Entries[key] = registrations;
    }
}

/// <summary>The active model's textures for one edited-holds placement: read at most once, and only when a photo is not cached.</summary>
/// <param name="modelId">The active model.</param>
/// <param name="textureSetKey">Its current texture set.</param>
/// <param name="load">Reads it with its textures.</param>
internal sealed class EditedPlacementSource(Guid modelId, string? textureSetKey, Func<Task<ActiveModel>> load)
{
    private ActiveModel? model;

    public Guid ModelId => modelId;

    public string? TextureSetKey => textureSetKey;

    /// <summary>Gets or sets how many photos were registered (not cached).</summary>
    public int Registered { get; set; }

    public async Task<List<RegistrationTexture>> TexturesAsync() => (model ??= await load()).Textures;
}
