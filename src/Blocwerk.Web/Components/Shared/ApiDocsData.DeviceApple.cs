// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The companion app's Sign in with Apple routes in the device-key reference.</summary>
internal static partial class ApiDocsData
{
    private const string AppleNonceNote =
        "Fetch a nonce first (POST /api/device-link/apple/nonce, anonymous, 20 a minute per address): it lives 5 minutes "
        + "and works once. Set the Apple request's nonce to its lowercase hex SHA-256 and send the nonce itself here.";

    private static ApiEndpointDoc[] DeviceAppleEndpoints =>
    [
        new("POST", "/api/v1/me/apple", "Link the Apple ID of a fresh Sign in with Apple to your account.",
            [],
            "{\n  \"identityToken\": \"<Apple identity token>\",\n  \"nonce\": \"<nonce from /api/device-link/apple/nonce>\"\n}",
            null,
            "Device key only; per-key write limit. 204 when linked (also when it already was), 400 { message } for a token or "
                + "nonce that does not verify, 409 { message } when the Apple ID is linked to another account here. Linking "
                + "another Apple ID replaces yours. " + AppleNonceNote),
        new("DELETE", "/api/v1/me/apple", "Unlink your Apple ID. Unlinking none is a no-op.",
            [],
            null,
            null,
            "Device key only; per-key write limit. 204. Keys already minted stay until revoked."),
        new("POST", "/api/device-link/apple", "Sign the device in with Apple: trade a fresh identity token for a device key.",
            [],
            "{\n  \"identityToken\": \"<Apple identity token>\",\n  \"nonce\": \"<nonce>\",\n  \"device\": \"Anna's iPhone\"\n}",
            "{\n  \"token\": \"bwk_…\",\n  \"keyId\": \"<guid>\",\n  \"name\": \"iPhone: Anna's iPhone\"\n}",
            "No key: anonymous, 10 a minute per address. Answers like POST /api/device-link/token: the same key name, an "
                + "older key of the same name revoked, at most 5 live device keys. 400 { error: \"invalid_grant\" } for a "
                + "token, nonce or device name that does not verify; 404 { message } when no account here is linked to the "
                + "Apple ID; 429 when the server's budget for failed attempts is spent (a success is never refused by it). "
                + AppleNonceNote),
    ];
}
