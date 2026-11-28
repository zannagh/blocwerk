// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Buffers.Binary;
using System.Security.Cryptography;
using Blocwerk.Core.AppleSignIn;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// <see cref="IAppleNonceCodec"/> over Data Protection with a purpose of its own: a nonce is 16 random bytes and the
/// expiry, protected (encrypted and authenticated) with the app's key ring, so it cannot be forged or extended.
/// </summary>
public sealed class DataProtectionAppleNonceCodec(IDataProtectionProvider provider) : IAppleNonceCodec
{
    /// <summary>The Data Protection purpose; no other payload is protected with it.</summary>
    public const string Purpose = "Blocwerk.AppleSignIn.Nonce.v1";

    private const int RandomLength = 16;

    private readonly IDataProtector protector = provider.CreateProtector(Purpose);

    /// <inheritdoc/>
    public string Issue(DateTimeOffset expiresAt)
    {
        var payload = new byte[RandomLength + sizeof(long)];
        RandomNumberGenerator.Fill(payload.AsSpan(0, RandomLength));
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(RandomLength), expiresAt.ToUnixTimeMilliseconds());
        return WebEncoders.Base64UrlEncode(protector.Protect(payload));
    }

    /// <inheritdoc/>
    public bool TryRead(string nonce, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        try
        {
            var payload = protector.Unprotect(WebEncoders.Base64UrlDecode(nonce));
            if (payload.Length != RandomLength + sizeof(long))
            {
                return false;
            }

            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(RandomLength)));
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
