namespace Blocwerk.Core.Abstractions;

/// <summary>How a <see cref="HoldOutlineResult"/> was obtained.</summary>
public enum HoldOutlineMethod
{
    /// <summary>Colour-distance segmentation against the locally sampled wall colour.</summary>
    Contour,

    /// <summary>GrabCut initialised from the seed, used when the contour pass leaked or came up empty.</summary>
    GrabCut,

    /// <summary>
    /// Nothing trustworthy was found (e.g. a wooden volume the colour of the wall): the result is the
    /// seed circle and its <see cref="HoldOutlineResult.ShapePoints"/> is null, so the hold keeps
    /// rendering as a plain circle.
    /// </summary>
    CircleFallback,
}
