// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.DeviceLinks;

/// <summary>
/// The validated query of <c>/device-link</c>: what the app asked for and where the answer goes. Only built by
/// <see cref="TryParse"/>, so a request in hand is one the server may answer with a redirect to the app.
/// </summary>
/// <param name="State">The app's opaque state, echoed back in the callback.</param>
/// <param name="CodeChallenge">The app's S256 PKCE challenge.</param>
/// <param name="DeviceName">The device's name, cleaned for display and for the key's name.</param>
public sealed record DeviceLinkRequest(string State, string CodeChallenge, string DeviceName)
{
    /// <summary>The one redirect the server ever sends a device link to: the companion app's own scheme.</summary>
    public const string RedirectUri = "blocwerk-companion://auth";

    /// <summary>The only PKCE method accepted. Plain is refused: it would put the verifier in the URL.</summary>
    public const string ChallengeMethod = "S256";

    /// <summary>The longest device name kept; longer names are cut, so a key name always fits.</summary>
    public const int MaxDeviceNameLength = 64;

    private const int MinStateLength = 16;
    private const int MaxStateLength = 128;

    /// <summary>
    /// Validates the query. Null (with a reason for the log) when anything is off: then the page shows an error and
    /// never redirects, so a crafted link cannot bounce the browser anywhere.
    /// </summary>
    public static DeviceLinkRequest? TryParse(
        string? state,
        string? codeChallenge,
        string? codeChallengeMethod,
        string? device,
        string? redirectUri,
        out string? reason)
    {
        reason = Check(state, codeChallenge, codeChallengeMethod, redirectUri);
        var name = CleanDeviceName(device);
        if (reason is null && name is null)
        {
            reason = "device name missing";
        }

        return reason is null ? new DeviceLinkRequest(state!, codeChallenge!, name!) : null;
    }

    /// <summary>
    /// The device name as shown and stored: control and formatting characters dropped, whitespace collapsed,
    /// trimmed and cut to <see cref="MaxDeviceNameLength"/>. Null when nothing is left.
    /// </summary>
    public static string? CleanDeviceName(string? device)
    {
        if (string.IsNullOrWhiteSpace(device))
        {
            return null;
        }

        var kept = new System.Text.StringBuilder(device.Length);
        foreach (var c in device)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                continue;
            }

            kept.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }

        var cleaned = string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > MaxDeviceNameLength)
        {
            cleaned = cleaned[..MaxDeviceNameLength].TrimEnd();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Check(string? state, string? challenge, string? method, string? redirectUri)
    {
        if (!string.Equals(redirectUri, RedirectUri, StringComparison.Ordinal))
        {
            return "redirect_uri not allowed";
        }

        if (!string.Equals(method, ChallengeMethod, StringComparison.Ordinal))
        {
            return "code_challenge_method must be S256";
        }

        if (!DeviceLinkPkce.IsWellFormedChallenge(challenge))
        {
            return "code_challenge malformed";
        }

        if (!DeviceLinkPkce.IsUrlSafe(state, MinStateLength, MaxStateLength))
        {
            return "state malformed";
        }

        return null;
    }
}
