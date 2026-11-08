namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Turns a detector seed (a centre + radius, i.e. today's circle) into the hold's real outline and a
/// <see cref="HoldFingerprint"/>. A wall photo is decoded ONCE per <see cref="OpenSession"/>; every
/// hold on that photo is then outlined against the already-decoded pixels, so outlining 300 holds on a
/// 4032×3024 photo costs one decode, not 300.
/// </summary>
/// <remarks>
/// The service is stateless and thread-safe (register it as a singleton). A session is NOT thread-safe;
/// use one session per thread, or outline sequentially.
/// </remarks>
public interface IHoldOutlineService
{
    /// <summary>
    /// Decodes an encoded wall photo (JPEG/PNG) and returns a session that outlines holds on it.
    /// </summary>
    /// <param name="encodedImage">The encoded photo bytes — the same bytes the detector ran on, so seed
    /// coordinates line up.</param>
    /// <returns>A disposable session; dispose it to free the decoded pixels.</returns>
    /// <exception cref="ArgumentException">The bytes could not be decoded as an image.</exception>
    IHoldOutlineSession OpenSession(byte[] encodedImage);
}
