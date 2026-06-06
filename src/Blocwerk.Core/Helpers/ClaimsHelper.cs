using System.Security.Claims;

namespace Blocwerk.Core.Helpers;

public static class ClaimsHelper
{
    public static ClaimsIdentity ClaimsIdentityFromUserNameAndId(string userName, string userId, string authenticationScheme = "Bearer")
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.NameIdentifier, userId),
            new("unique_name", userName),
            new("nameid", userId),
        };

        return new ClaimsIdentity(claims, authenticationScheme);
    }

    public static string ToUserId(this ClaimsIdentity identity)
    {
        return identity.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? identity.FindFirst("nameid")?.Value
               ?? string.Empty;
    }

    public static string ToUserName(this ClaimsIdentity identity)
    {
        return identity.FindFirst(ClaimTypes.Name)?.Value
               ?? identity.FindFirst("unique_name")?.Value
               ?? string.Empty;
    }

    public static string ToUserIdentifier(this ClaimsIdentity identity)
    {
        var name = identity.ToUserName();
        var id = identity.ToUserId();
        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(id)
            ? string.Empty
            : $"{name}__{id}";
    }
}
