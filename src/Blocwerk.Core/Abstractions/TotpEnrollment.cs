namespace Blocwerk.Core.Abstractions;

/// <summary>
/// The one-time material handed back when a user begins TOTP enrolment: the freshly generated Base32
/// secret (for manual authenticator entry), the <c>otpauth://</c> provisioning URI, and a QR PNG that
/// encodes that URI. The protected form of the secret is already persisted on the user by the time this
/// is returned; these values are shown once and never stored in the clear.
/// </summary>
/// <param name="Secret">The Base32-encoded shared secret, for manual entry into an authenticator app.</param>
/// <param name="OtpAuthUri">The <c>otpauth://totp/…</c> provisioning URI the QR encodes.</param>
/// <param name="QrPng">A PNG image (raw bytes) of the QR code for the provisioning URI.</param>
public record TotpEnrollment(string Secret, string OtpAuthUri, byte[] QrPng);
