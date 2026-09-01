using System.Globalization;
using System.Text;
using QRCoder;

namespace Blocwerk.Web.State;

/// <summary>
/// Renders a pairing URL as an INLINE SVG QR code.
/// </summary>
/// <remarks>
/// Inline and server-side, for two reasons. The app's Content-Security-Policy allows images only
/// from <c>'self'</c>, <c>data:</c> and <c>blob:</c>, so a hosted QR service was never an option —
/// and a tablet bolted to a gym wall may well be on a network that cannot reach one anyway. Inline
/// SVG also scales to whatever the tablet's screen is without a second request, and picks up the
/// page's colours through <c>currentColor</c>, so the code stays legible in either theme.
/// <para>
/// The matrix comes from QRCoder, which the solution already depends on for the TOTP enrolment code
/// (<c>TotpService</c>); this walks its <c>ModuleMatrix</c> rather than using its own SVG renderer so
/// the output has no <c>System.Drawing</c> dependency and can name its own colours.
/// </para>
/// </remarks>
public static class KioskPairingQrCode
{
    /// <summary>
    /// The SVG for <paramref name="payload"/>, sized by its viewBox so the caller controls the
    /// rendered size with CSS alone.
    /// </summary>
    /// <remarks>
    /// Error correction level Q (~25%): a wall tablet is a glossy screen photographed at an angle in
    /// gym lighting, and the payload is short enough that the extra redundancy costs nothing anybody
    /// will notice.
    /// </remarks>
    public static string ToSvg(string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);

        var matrix = data.ModuleMatrix;
        var size = matrix.Count;

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" ");
        svg.Append("shape-rendering=\"crispEdges\" role=\"img\" aria-label=\"Pairing QR code\" class=\"kiosk-pair-qr-svg\">");

        // The quiet zone is part of the matrix, and it has to be PAINTED: an SVG with a transparent
        // background sits on whatever the page is, and a dark theme would leave the code inverted
        // and unscannable. So a light rectangle first, dark modules on top, both regardless of theme.
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"{size}\" height=\"{size}\" fill=\"#ffffff\"/>");
        svg.Append("<path fill=\"#000000\" d=\"");

        for (var y = 0; y < size; y++)
        {
            var row = matrix[y];

            // Runs of adjacent dark modules become one horizontal rectangle instead of one per
            // module, which is what keeps the markup a few KB rather than a few dozen.
            var x = 0;
            while (x < size)
            {
                if (!row[x])
                {
                    x++;
                    continue;
                }

                var start = x;
                while (x < size && row[x])
                {
                    x++;
                }

                svg.Append(CultureInfo.InvariantCulture, $"M{start} {y}h{x - start}v1h-{x - start}z");
            }
        }

        svg.Append("\"/></svg>");
        return svg.ToString();
    }
}
