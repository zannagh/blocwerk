// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.Extensions.Configuration;

namespace Blocwerk.Authentication.Apple;

/// <summary>
/// Sign in with Apple on the web: the Services ID, team, key id and private key, read from four environment variables.
/// </summary>
/// <remarks>
/// The provider is on only when all four are set (the cluster mounts them from the Secret <c>blocwerk-apple-signin</c>).
/// Without them, in local development and CI, the button is hidden and every Apple route falls back as for a disabled
/// provider. The private key is never logged.
/// </remarks>
public sealed class AppleWebSettings
{
    /// <summary>The PEM (or bare base64 PKCS#8) of the Sign in with Apple key, the content of the .p8 file.</summary>
    public const string PrivateKeyVariable = "APPLE_SIGNIN_PRIVATE_KEY";

    /// <summary>The id of that key in the Apple developer account.</summary>
    public const string KeyIdVariable = "APPLE_SIGNIN_KEY_ID";

    /// <summary>The Apple developer team id.</summary>
    public const string TeamIdVariable = "APPLE_SIGNIN_TEAM_ID";

    /// <summary>The Services ID: the web client id, and the audience of the identity tokens Apple issues for it.</summary>
    public const string ServicesIdVariable = "APPLE_SIGNIN_SERVICES_ID";

    /// <summary>The provider key, as in <c>/account/external?provider=apple</c>.</summary>
    public const string Provider = "apple";

    /// <summary>The path Apple posts the authorization back to; registered as a Return URL per host.</summary>
    public const string CallbackPath = "/oauth-callback/apple";

    /// <summary>Gets the private key text, empty when unset.</summary>
    public string PrivateKey { get; init; } = string.Empty;

    /// <summary>Gets the key id, empty when unset.</summary>
    public string KeyId { get; init; } = string.Empty;

    /// <summary>Gets the team id, empty when unset.</summary>
    public string TeamId { get; init; } = string.Empty;

    /// <summary>Gets the Services ID, empty when unset.</summary>
    public string ServicesId { get; init; } = string.Empty;

    /// <summary>Gets Apple's authorize endpoint.</summary>
    public string AuthorizeUrl { get; init; } = "https://appleid.apple.com/auth/authorize";

    /// <summary>Gets Apple's token endpoint.</summary>
    public string TokenUrl { get; init; } = "https://appleid.apple.com/auth/token";

    /// <summary>Gets a value indicating whether all four values are present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(ServicesId);

    /// <summary>Reads the four variables from configuration (environment variables keep their names there).</summary>
    public static AppleWebSettings FromConfiguration(IConfiguration configuration) => new()
    {
        PrivateKey = configuration[PrivateKeyVariable]?.Trim() ?? string.Empty,
        KeyId = configuration[KeyIdVariable]?.Trim() ?? string.Empty,
        TeamId = configuration[TeamIdVariable]?.Trim() ?? string.Empty,
        ServicesId = configuration[ServicesIdVariable]?.Trim() ?? string.Empty,
    };
}
