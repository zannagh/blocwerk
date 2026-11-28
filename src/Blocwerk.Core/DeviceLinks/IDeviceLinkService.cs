// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.DeviceLinks;

/// <summary>
/// Links the companion app to an account: a signed-in user confirms in the browser, which makes a one-time code, and
/// the app exchanges that code with its PKCE verifier for a personal API key.
/// </summary>
public interface IDeviceLinkService
{
    /// <summary>
    /// Makes a one-time code for <paramref name="userId"/>, bound to the request's challenge and device. Refused in a
    /// session that was itself signed in with an API key, like every other way to mint one.
    /// </summary>
    Task<string> CreateCodeAsync(Guid userId, DeviceLinkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Uses the code: null unless it exists, is unexpired and unused, the verifier matches its challenge and the
    /// device matches. The first attempt uses the code whatever the outcome, so a wrong verifier burns it.
    /// </summary>
    Task<DeviceLinkExchange?> ExchangeAsync(string? code, string? verifier, string? device, CancellationToken ct = default);
}
