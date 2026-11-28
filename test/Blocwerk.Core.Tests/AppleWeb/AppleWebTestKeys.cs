// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;
using Blocwerk.Authentication.Apple;

namespace Blocwerk.Core.Tests.AppleWeb;

/// <summary>A throwaway P-256 key per test run, standing in for the Sign in with Apple .p8 key, and settings around it.</summary>
internal static class AppleWebTestKeys
{
    public const string TeamId = "TEAM123456";
    public const string KeyId = "KEY1234567";
    public const string ServicesId = "app.example.web";

    /// <summary>A fresh key and its PKCS#8 PEM, as the .p8 file holds it.</summary>
    public static (ECDsa Key, string Pem) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Settings with all four values set, for <paramref name="pem"/>.</summary>
    public static AppleWebSettings Settings(string pem, string tokenUrl = "https://apple.test/auth/token") => new()
    {
        PrivateKey = pem,
        KeyId = KeyId,
        TeamId = TeamId,
        ServicesId = ServicesId,
        TokenUrl = tokenUrl,
    };
}
