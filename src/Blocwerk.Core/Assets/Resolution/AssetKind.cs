// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Resolution;

/// <summary>Which stored column a hashed asset (<c>/api/walls/{w}/a/{sha}</c>) resolved to.</summary>
public enum AssetKind
{
    /// <summary>A panel's committed photo (<c>WallPanel.Photo</c>), of any generation.</summary>
    Panel,

    /// <summary>The retiring single-image wall photo (<c>Wall.Photo</c>).</summary>
    WallPhoto,

    /// <summary>A photo a reset archived (<c>WallReset.PreviousPhoto</c>).</summary>
    ResetPhoto,

    /// <summary>An uploaded gallery image (<c>WallImage</c>, a file).</summary>
    Gallery,

    /// <summary>A facet texture of a geometry model (a file).</summary>
    Texture,

    /// <summary>A facet texture's coverage mask (a PNG file).</summary>
    Mask,

    /// <summary>A splat scene: the full one, the legacy mobile copy or a level of the LOD ladder (a file).</summary>
    Splat,

    /// <summary>A beta video's poster frame (<c>BetaVideo.Thumbnail</c>).</summary>
    VideoThumbnail,
}
