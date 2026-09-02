using Blocwerk.Core.Services;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The width an image element asks for before JavaScript has measured anything.
/// </summary>
/// <remarks>
/// The stored photo is the camera original and can be tens of megapixels, so painting a wall from
/// it costs megabytes the viewport cannot show. Markup therefore requests a modest rendition and
/// <c>wwwroot/js/image-res.js</c> upgrades it — on landing, to whatever the element's CSS size
/// times the device pixel ratio actually needs, and again as the user zooms in. Starting from a
/// real width rather than from nothing means the page still shows a good photo if that script never
/// runs, and on a phone (the common case) the first request is usually already the right one.
/// </remarks>
public static class ResponsiveImage
{
    /// <summary>
    /// Initial rendition width. 1280 covers a phone at fit on a 2-3x display without a second
    /// request, and is small enough that a desktop's upgrade to 1920/2560 is not a wasted download
    /// worth worrying about.
    /// </summary>
    public const int InitialWidth = 1280;

    /// <summary>
    /// <paramref name="url"/> with the initial width appended, preserving any query it already
    /// carries (the share token, in practice).
    /// </summary>
    public static string Initial(string url) => AtWidth(url, InitialWidth);

    /// <summary>
    /// <paramref name="url"/> asking for the <paramref name="width"/> rendition. Returns it
    /// unchanged for a width the byte routes will not render, so a bad constant degrades into the
    /// original rather than into a 404.
    /// </summary>
    public static string AtWidth(string url, int width)
    {
        if (!ImageVariants.IsAllowed(width))
        {
            return url;
        }

        return url + (url.Contains('?') ? '&' : '?') + "w=" + width;
    }
}
