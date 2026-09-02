using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The identity half of a cached variant's key, for every image the byte routes can serve.
/// </summary>
/// <remarks>
/// Exists so the routes and the cache warmer (<c>ImageVariantWarmer</c>) derive it from ONE place.
/// The identity is hashed into the cache file path, so a warmer that spelled these parts even
/// slightly differently would fill the disk with entries no request ever looks up — which reads as
/// a warmer that silently does nothing.
/// </remarks>
public static class ImageIdentity
{
    /// <summary>The wall's current photo.</summary>
    public static object?[] WallPhoto(Guid wallId) => [wallId, "live"];

    /// <summary>The wall photo as it stood at <paramref name="generation"/>.</summary>
    public static object?[] WallGenerationPhoto(Guid wallId, int generation) => [wallId, generation];

    /// <summary>The staged photo of a wall update in progress.</summary>
    public static object?[] StagedWallPhoto(Guid wallId) => [wallId, "staged"];

    /// <summary>A big wall's per-panel photo, live or staged.</summary>
    public static object?[] PanelPhoto(Guid panelId, string slot) => [panelId, slot];

    /// <summary>The live slot name used by the panel photo routes.</summary>
    public const string LiveSlot = "live";

    /// <summary>The staged slot name used by the panel photo routes.</summary>
    public const string StagedSlot = "staged";

    /// <summary>An uploaded gallery image, whose bytes live in the wall-image file store.</summary>
    public static object?[] UploadedGalleryImage(Guid imageId) => [imageId];

    /// <summary>
    /// A gallery item backed by a database blob. The source is the route segment, and the gallery
    /// builds its URLs with the enum name lowercased (<c>WallGalleryPanel.razor</c>) — so that is
    /// what the warmer must key on. The route itself still hashes whatever segment the caller sent,
    /// which is left alone: normalising it there would move every existing ETag.
    /// </summary>
    public static object?[] LegacyGalleryImage(Guid wallId, WallGallerySource source, Guid sourceId) =>
        [wallId, source.ToString().ToLowerInvariant(), sourceId];
}
