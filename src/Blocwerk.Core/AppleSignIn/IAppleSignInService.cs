// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.AppleSignIn;

/// <summary>
/// Native Sign in with Apple for the companion app: the server issues a nonce, the app links the Apple ID to the account
/// it is signed in to, and later trades a fresh Apple identity token for a device key.
/// </summary>
public interface IAppleSignInService
{
    /// <summary>Issues a single-use nonce for one Apple request.</summary>
    Task<string> CreateNonceAsync(CancellationToken ct = default);

    /// <summary>
    /// Links the token's Apple ID to <paramref name="userId"/> from the device key <paramref name="apiKeyId"/>: only while
    /// the account has no Apple ID yet, and only from a key minted within <see cref="AppleSignInService.LinkKeyMaxAge"/>,
    /// so a key someone held briefly cannot quietly add their own Apple ID later.
    /// </summary>
    Task<AppleLinkOutcome> LinkAsync(
        Guid userId,
        Guid apiKeyId,
        string? identityToken,
        string? nonce,
        CancellationToken ct = default);

    /// <summary>The account's linked Apple ID, or null.</summary>
    Task<Entities.AppleLink?> FindLinkAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Links an Apple ID already verified by the caller (the web sign-in's code exchange) to <paramref name="userId"/>,
    /// with the same rules as <see cref="LinkAsync"/>: it replaces an Apple ID linked before, and is refused when
    /// another account holds this one.
    /// </summary>
    Task<AppleLinkOutcome> LinkSubjectAsync(Guid userId, string subject, CancellationToken ct = default);

    /// <summary>
    /// Removes the account's Apple ID; false when none was linked. Throws <see cref="AppleOnlySignInException"/> when
    /// the Apple ID is the account's only way in (<see cref="HasOtherSignInAsync"/> is false).
    /// </summary>
    Task<bool> UnlinkAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// True when the account can sign in without its Apple ID: a password, or a GitHub, Google or Microsoft identity.
    /// </summary>
    Task<bool> HasOtherSignInAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True when an Apple ID is linked to the account.</summary>
    Task<bool> IsLinkedAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Mints a device key for the account the token's Apple ID is linked to, exactly as the device link's code exchange
    /// does (same key name, same-name revocation, the live-key cap). A kiosk session is refused by the key service.
    /// </summary>
    Task<AppleSignInResult> SignInAsync(string? identityToken, string? nonce, string? device, CancellationToken ct = default);
}
