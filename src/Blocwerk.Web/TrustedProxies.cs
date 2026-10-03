using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Blocwerk.Web;

/// <summary>
/// Applies <c>Blocwerk:Server:TrustedProxies</c> to the forwarded-headers middleware.
/// </summary>
/// <remarks>
/// Empty keeps the historical behaviour — both trust lists cleared, so ANY sender's
/// <c>X-Forwarded-For</c> is believed — which is safe only while the app is reachable solely through
/// the reverse proxy. Listing the proxy makes the client IP (and with it the per-IP rate limit on the
/// API-key login, and every log line) trustworthy even if the port is ever exposed directly.
/// </remarks>
public static class TrustedProxies
{
    public static void Apply(ForwardedHeadersOptions options, IReadOnlyList<string> entries)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        foreach (var entry in entries)
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                options.KnownIPNetworks.Add(ParseNetwork(entry));
            }
            else
            {
                options.KnownProxies.Add(ParseAddress(entry));
            }
        }
    }

    // A bad entry fails startup: silently dropping it would leave the proxy untrusted and every
    // client looking like it came from the proxy's address.
    private static System.Net.IPNetwork ParseNetwork(string entry) =>
        System.Net.IPNetwork.TryParse(entry, out var network)
            ? network
            : throw new InvalidOperationException($"Blocwerk:Server:TrustedProxies: '{entry}' is not a CIDR network.");

    private static IPAddress ParseAddress(string entry) =>
        IPAddress.TryParse(entry, out var address)
            ? address
            : throw new InvalidOperationException($"Blocwerk:Server:TrustedProxies: '{entry}' is not an IP address.");
}
