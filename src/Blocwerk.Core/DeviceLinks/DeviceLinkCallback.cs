// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.DeviceLinks;

/// <summary>
/// Builds the redirect back to the app. The target is the constant <see cref="DeviceLinkRequest.RedirectUri"/>, never
/// a value from the request, and every parameter is escaped, so nothing a caller sends can change where it goes.
/// </summary>
public static class DeviceLinkCallback
{
    /// <summary>The callback that hands the app its one-time code.</summary>
    public static string WithCode(string code, string state) =>
        $"{DeviceLinkRequest.RedirectUri}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}";

    /// <summary>The callback that tells the app the user said no.</summary>
    public static string Denied(string state) =>
        $"{DeviceLinkRequest.RedirectUri}?error=access_denied&state={Uri.EscapeDataString(state)}";
}
