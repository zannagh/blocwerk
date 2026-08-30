using Microsoft.AspNetCore.DataProtection;
using OtpNet;
using QRCoder;

namespace Blocwerk.Authentication.Services;

/// <summary>
/// <see cref="ITotpService"/> over Otp.NET (codes + Base32) and QRCoder (QR PNG). The shared secret is
/// encrypted at rest with a dedicated DataProtection protector ("blocwerk.totp"), whose key ring is
/// persisted alongside the auth keys — so secrets enrolled before a redeploy stay decryptable.
/// </summary>
public class TotpService : ITotpService
{
    private const string SecretPurpose = "blocwerk.totp";
    private const string Issuer = "Blocwerk";
    private const int SecretBytes = 20;
    private const int Digits = 6;
    private const int PeriodSeconds = 30;

    private readonly IDataProtector protector;

    public TotpService(IDataProtectionProvider dataProtectionProvider)
    {
        protector = dataProtectionProvider.CreateProtector(SecretPurpose);
    }

    public string GenerateSecret()
    {
        var key = KeyGeneration.GenerateRandomKey(SecretBytes);
        return Base32Encoding.ToString(key);
    }

    public string BuildOtpAuthUri(string secret, string accountLabel)
    {
        var label = Uri.EscapeDataString(accountLabel);
        var normalizedSecret = NormalizeSecret(secret);
        return $"otpauth://totp/{Issuer}:{label}"
               + $"?secret={normalizedSecret}"
               + $"&issuer={Issuer}"
               + $"&digits={Digits}"
               + $"&period={PeriodSeconds}";
    }

    public byte[] BuildQrPng(string otpAuthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpAuthUri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(10);
    }

    public bool Verify(string secret, string code) => Verify(secret, code, out _);

    public bool Verify(string secret, string code, out long matchedStep)
    {
        matchedStep = 0;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        try
        {
            var key = Base32Encoding.ToBytes(NormalizeSecret(secret));
            var totp = new Totp(key, step: PeriodSeconds, totpSize: Digits);

            // A ±1 step window tolerates modest clock skew between server and authenticator. The matched
            // step is surfaced so the caller can reject replay of an already-used code inside that window.
            return totp.VerifyTotp(code.Trim(), out matchedStep, new VerificationWindow(previous: 1, future: 1));
        }
        catch (Exception)
        {
            // A malformed secret or code must read as "wrong code", never surface as an error.
            return false;
        }
    }

    public string Protect(string secret) => protector.Protect(secret);

    public string Unprotect(string protectedSecret) => protector.Unprotect(protectedSecret);

    // Authenticator apps expect an un-padded Base32 secret with no whitespace.
    private static string NormalizeSecret(string secret) =>
        secret.Replace(" ", string.Empty).Replace("=", string.Empty).ToUpperInvariant();
}
