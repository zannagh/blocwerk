// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Assets;

/// <summary>A blob column and the column holding its <see cref="ContentHash"/>.</summary>
/// <param name="Entity">The entity type.</param>
/// <param name="Bytes">The <c>byte[]</c> property.</param>
/// <param name="Hash">The <c>byte[]</c> property that holds the hash of <paramref name="Bytes"/>.</param>
public sealed record ContentHashColumn(Type Entity, string Bytes, string Hash)
{
    /// <summary>
    /// Every database blob that is served as an image, hashed on each save by <c>ContentHashStamp</c>. A new image
    /// <c>byte[]</c> goes here; the guard test fails until it does (or is listed as deliberately unhashed there).
    /// </summary>
    public static IReadOnlyList<ContentHashColumn> InDatabase { get; } =
    [
        new(typeof(WallPanel), nameof(WallPanel.Photo), nameof(WallPanel.PhotoSha256)),
        new(typeof(WallPanel), nameof(WallPanel.StagedPhoto), nameof(WallPanel.StagedPhotoSha256)),
        new(typeof(Wall), nameof(Wall.Photo), nameof(Wall.PhotoSha256)),
        new(typeof(Wall), nameof(Wall.StagedPhoto), nameof(Wall.StagedPhotoSha256)),
        new(typeof(WallReset), nameof(WallReset.PreviousPhoto), nameof(WallReset.PreviousPhotoSha256)),
        new(typeof(User), nameof(User.AvatarImage), nameof(User.AvatarSha256)),
        new(typeof(BetaVideo), nameof(BetaVideo.Thumbnail), nameof(BetaVideo.ThumbnailSha256)),
    ];

    /// <summary>Whether <paramref name="property"/> of <paramref name="entity"/> is one of the stamped hash columns.</summary>
    public static bool IsHash(Type entity, string property) => InDatabase.Any(c => c.Entity == entity && c.Hash == property);
}
