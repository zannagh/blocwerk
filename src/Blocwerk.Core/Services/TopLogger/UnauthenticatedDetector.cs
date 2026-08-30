using System.Text.Json;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Detects the TopLogger <c>UNAUTHENTICATED</c> GraphQL error code, which the API
/// returns (with HTTP 200) when a bearer token is no longer accepted.
/// </summary>
internal static class UnauthenticatedDetector
{
    public static bool IsUnauthenticated(IReadOnlyList<GraphQlError>? errors)
    {
        if (errors is null)
        {
            return false;
        }

        foreach (GraphQlError error in errors)
        {
            if (error.Extensions is not null
                && error.Extensions.TryGetValue("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.String
                && string.Equals(code.GetString(), "UNAUTHENTICATED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
