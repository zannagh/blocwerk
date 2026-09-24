// <copyright file="IPhotoTextureMatcher.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Finds point correspondences between a wall photo and a flattened facet texture of the wall's 3D model
/// (feature matching). Encoded images in, point pairs out: which pairs describe one plane, and whether that
/// plane is trustworthy, is decided in Core (<c>PhotoTextureRegistration</c>). Implemented with OpenCV in
/// <c>Blocwerk.HoldDetection</c>; a host without it cannot place holds by texture registration.
/// </summary>
public interface IPhotoTextureMatcher
{
    /// <summary>Decodes a photo once, for matching it against several textures.</summary>
    /// <param name="encodedPhoto">The photo (JPEG/PNG/WebP), decoded WITHOUT its EXIF orientation, like hold positions are.</param>
    /// <returns>The session; dispose it.</returns>
    /// <exception cref="ArgumentException">The photo cannot be decoded.</exception>
    IPhotoTextureSession OpenPhoto(byte[] encodedPhoto);
}

/// <summary>One decoded photo, matched against textures one at a time.</summary>
public interface IPhotoTextureSession : IDisposable
{
    /// <summary>Gets the photo width in pixels.</summary>
    int Width { get; }

    /// <summary>Gets the photo height in pixels.</summary>
    int Height { get; }

    /// <summary>Matches the photo against one texture.</summary>
    /// <param name="encodedTexture">The facet texture.</param>
    /// <param name="encodedMask">Its coverage mask (0 = no photo there), or null when every pixel counts.</param>
    /// <param name="textureMmPerPx">The texture's resolution, so it can be matched at a physical scale close to the photo's.</param>
    /// <param name="seed">
    /// A predicted photo px → texture px homography (row-major 3×3) to match around instead of searching the
    /// whole texture; null runs the coarse search.
    /// </param>
    /// <returns>The correspondences, photo px → texture px, both at full resolution.</returns>
    PhotoTextureMatch Match(byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed = null);
}

/// <summary>What one photo × texture match found.</summary>
/// <param name="Pairs">Correspondences: source = photo px, destination = texture px (full resolution).</param>
/// <param name="CoarseInliers">Inliers of the coarse whole-image homography that guided the fine matching (0 when it failed).</param>
/// <param name="Failure">Why no pairs came back, or null.</param>
public sealed record PhotoTextureMatch(IReadOnlyList<PointCorrespondence> Pairs, int CoarseInliers, string? Failure)
{
    /// <summary>A match that found nothing usable.</summary>
    /// <param name="reason">Why.</param>
    /// <param name="coarseInliers">Coarse inliers, if the coarse stage got that far.</param>
    /// <returns>The empty match.</returns>
    public static PhotoTextureMatch Failed(string reason, int coarseInliers = 0) => new([], coarseInliers, reason);
}
