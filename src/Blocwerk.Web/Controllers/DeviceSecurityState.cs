// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>
/// The web profile's "Sign-in and security" section as read-only state, for the companion app: what is set up, never a
/// secret. Changing any of it stays on the web (see <c>ApiKeySessionGuard</c>).
/// </summary>
/// <param name="Email">The account's e-mail address, or null.</param>
/// <param name="EmailVerified">True when the address was confirmed with a code.</param>
/// <param name="HasPassword">True when password sign-in is set up.</param>
/// <param name="LoginUsername">The password sign-in's username, or null.</param>
/// <param name="TotpEnabled">True when two-factor authentication is on.</param>
/// <param name="LinkedProviders">The linked sign-in providers: github, google, microsoft, apple.</param>
/// <param name="ManageUrl">Where the app sends the person to change any of it: the web profile's account tab.</param>
public sealed record DeviceSecurityState(
    string? Email,
    bool EmailVerified,
    bool HasPassword,
    string? LoginUsername,
    bool TotpEnabled,
    IReadOnlyList<string> LinkedProviders,
    string ManageUrl);
