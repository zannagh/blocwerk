namespace Blocwerk.Core.Enums;

/// <summary>Where the bytes of a gallery item have to be fetched from.</summary>
public enum WallGallerySource
{
    /// <summary>An uploaded <c>WallImage</c> row; the bytes live in the wall-image file store.</summary>
    Uploaded = 0,

    /// <summary>The wall's current photo, stored as a byte array on the wall row.</summary>
    WallPhoto = 1,

    /// <summary>The photo a wall reset retired, stored as a byte array on the reset row.</summary>
    ResetPhoto = 2,
}
