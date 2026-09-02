namespace Blocwerk.Core.Services;

/// <summary>
/// The fixed ladder of widths an image byte route will render. Closed rather than open on purpose:
/// an arbitrary <c>?w=</c> would let anyone pin a CPU decoding and rescaling a 50 MP wall photo at
/// every width between 1 and 20000, and fill the variant cache while doing it.
/// </summary>
public static class ImageVariants
{
    /// <summary>
    /// JPEG quality for a rendition. Deliberately high: these are climbing-wall photos that get
    /// zoomed into to tell one hold from another, so ringing around hold edges matters far more
    /// than the last few kilobytes.
    /// </summary>
    public const int Quality = 88;

    /// <summary>
    /// Renderable widths, ascending. Chosen against the viewport: 640 is a phone at fit, 1280 a
    /// phone at DPR 2-3 or a tablet, 1920/2560 what zooming into a wall to pick a hold needs.
    /// Above 2560 the route serves the stored original, which is the sharpest thing there is.
    /// </summary>
    public static readonly int[] Widths = [640, 1280, 1920, 2560];

    /// <summary>Whether <paramref name="width"/> is one this app will render.</summary>
    public static bool IsAllowed(int width) => Array.IndexOf(Widths, width) >= 0;

    /// <summary>The largest width on the ladder.</summary>
    public static int MaxWidth => Widths[^1];
}
