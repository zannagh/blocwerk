using System.Net;
using System.Text.Json;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Detects TopLogger rate-limiting. TopLogger signals throttling either as an
/// HTTP <c>429 Too Many Requests</c> status or, with HTTP 200, as a GraphQL error
/// whose message reads <c>"ThrottlerException: Too Many Requests"</c> or whose
/// <c>extensions.code</c> indicates throttling. Mirrors <see cref="UnauthenticatedDetector"/>.
/// </summary>
internal static class ThrottleDetector
{
    private static readonly string[] MessageMarkers =
    {
        "ThrottlerException",
        "Too Many Requests",
    };

    private static readonly string[] CodeMarkers =
    {
        "THROTTLE",
        "TOO_MANY_REQUESTS",
    };

    public static bool IsThrottled(HttpStatusCode status)
    {
        return status == HttpStatusCode.TooManyRequests;
    }

    public static bool IsThrottled(IReadOnlyList<GraphQlError>? errors)
    {
        if (errors is null)
        {
            return false;
        }

        foreach (GraphQlError error in errors)
        {
            if (MatchesMessage(error.Message) || MatchesCode(error))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        foreach (string marker in MessageMarkers)
        {
            if (message.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCode(GraphQlError error)
    {
        if (error.Extensions is null
            || !error.Extensions.TryGetValue("code", out JsonElement code)
            || code.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value = code.GetString();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (string marker in CodeMarkers)
        {
            if (value.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
