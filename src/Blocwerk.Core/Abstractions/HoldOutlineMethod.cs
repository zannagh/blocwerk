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

    /// <summary>Cut out by the segmentation model as the object in the hold's detector box.</summary>
    Segmenter,

    /// <summary>
    /// A rough shape: nothing traced was consistent with the hold's detector box (only a fragment of it, a bolt hole,
    /// half the hold), so the outline is the smooth ellipse of the box, at low confidence.
    /// </summary>
    RoughShape,
}
