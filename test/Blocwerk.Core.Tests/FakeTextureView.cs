using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Tests;

/// <summary>
/// One photo's view of one facet texture for <see cref="FakePhotoTextureMatcher"/>: correspondences are generated
/// on a grid over the photo's x-range [<paramref name="X0"/>, <paramref name="X1"/>] through the exact mapping.
/// </summary>
/// <param name="Photo">First byte of the photo.</param>
/// <param name="Texture">First byte of the texture.</param>
/// <param name="X0">Left edge of the view in photo px.</param>
/// <param name="X1">Right edge of the view in photo px.</param>
/// <param name="Step">Grid step in photo px (smaller = more inliers).</param>
/// <param name="PhotoToTexture">The true mapping, photo px → texture px.</param>
/// <param name="NeedsSeed">
/// When true the coarse search misses this view: it is only found around a seed (a predicted photo px → texture
/// px homography) that lands within <see cref="FakePhotoTextureMatcher.SeedTolerancePx"/> of the true mapping.
/// </param>
internal sealed record FakeTextureView(
    byte Photo, byte Texture, double X0, double X1, double Step, PlaneHomography PhotoToTexture, bool NeedsSeed = false);
