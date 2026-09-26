using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The session of <see cref="FakePhotoTextureMatcher"/>: textures are identified by their first byte.</summary>
internal sealed class FakePhotoTextureSession(FakePhotoTextureMatcher matcher, byte photo) : IPhotoTextureSession
{
    public int Width => FakePhotoTextureMatcher.Width;

    public int Height => FakePhotoTextureMatcher.Height;

    public PhotoTextureMatch Match(
        byte[] encodedTexture, byte[]? encodedMask, double textureMmPerPx, double[]? seed = null, PhotoTextureAttempt? attempt = null) =>
        matcher.Match(photo, encodedTexture[0], seed, attempt);

    public void Dispose()
    {
    }
}
