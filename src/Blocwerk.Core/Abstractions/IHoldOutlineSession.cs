namespace Blocwerk.Core.Abstractions;

/// <summary>
/// One decoded wall photo, ready to outline any number of holds on it. Created by
/// <see cref="IHoldOutlineService.OpenSession"/>; holds the decoded pixels until disposed.
/// </summary>
public interface IHoldOutlineSession : IDisposable
{
    /// <summary>Gets the decoded image width in pixels.</summary>
    int ImageWidth { get; }

    /// <summary>Gets the decoded image height in pixels.</summary>
    int ImageHeight { get; }

    /// <summary>
    /// Outlines the hold around <paramref name="seed"/>. Never throws for a seed on or near the image:
    /// when segmentation cannot be trusted the result is a <see cref="HoldOutlineMethod.CircleFallback"/>
    /// with a low <see cref="HoldOutlineResult.Confidence"/> rather than a leaking blob.
    /// </summary>
    /// <param name="seed">The detector's seed for one hold.</param>
    /// <returns>The outline, its hold-convention shape points and a fingerprint.</returns>
    HoldOutlineResult Outline(HoldSeed seed);
}
