// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>The <c>AppleSignIn</c> configuration section: what a native Sign in with Apple token must be issued for.</summary>
public sealed class AppleSignInSettings
{
    /// <summary>The configuration section's name.</summary>
    public const string SectionName = "AppleSignIn";

    /// <summary>The iOS app's bundle id, the audience of the tokens it receives from Apple.</summary>
    public const string IosBundleId = "me.zannagh.blocwerk.ios";

    /// <summary>The web Sign in with Apple's Services ID, the audience of tokens from the web flow.</summary>
    public const string WebServicesId = "me.zannagh.blocwerk.web";

    /// <summary>
    /// Gets or sets the audiences (<c>aud</c>) a token may carry: the iOS app's bundle id and the web's Services ID by
    /// default. Configured as <c>AppleSignIn__AllowedAudiences__0</c>, <c>__1</c>, …; a configured list replaces the
    /// default.
    /// </summary>
    public string[] AllowedAudiences { get; set; } = [IosBundleId, WebServicesId];

    /// <summary>Gets or sets where Apple publishes its signing keys.</summary>
    public string KeysUrl { get; set; } = "https://appleid.apple.com/auth/keys";
}
