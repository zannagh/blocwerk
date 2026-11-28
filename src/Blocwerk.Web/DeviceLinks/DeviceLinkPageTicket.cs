// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.DeviceLinks;
using Microsoft.AspNetCore.DataProtection;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// A protected stamp the consent page puts in its form: who opened it, for which request (state, challenge and device
/// name), and when. Link makes a code
/// only while the stamp is younger than <see cref="DeviceLinkPolicy.PageLifetime"/>, so a page left open (or one a
/// stranger's link opened long ago) has to be opened again, with its match code, before it links anything; and a post
/// whose state or device name differs from what the page showed links nothing either.
/// </summary>
/// <remarks>The fields are kept as a JSON array, so no separator inside a field can shift the others.</remarks>
public sealed class DeviceLinkPageTicket(IDataProtectionProvider protection, TimeProvider? clock = null)
{
    private const string Purpose = "Blocwerk.DeviceLink.PageTicket.v2";

    private readonly IDataProtector protector = protection.CreateProtector(Purpose);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>A stamp for <paramref name="userId"/> opening the page for <paramref name="request"/> now.</summary>
    public string Issue(Guid userId, DeviceLinkRequest request)
    {
        var issued = Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return protector.Protect(JsonSerializer.Serialize(Fields(userId, request, issued)));
    }

    /// <summary>True when the stamp is genuine, names this user and exactly this request, and is still fresh.</summary>
    public bool IsValid(string? ticket, Guid userId, DeviceLinkRequest request)
    {
        if (string.IsNullOrEmpty(ticket))
        {
            return false;
        }

        string plain;
        try
        {
            plain = protector.Unprotect(ticket);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }

        var parts = Parse(plain);
        if (parts is not { Length: 5 }
            || !parts.AsSpan(0, 4).SequenceEqual(Fields(userId, request, parts[4]).AsSpan(0, 4))
            || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedMs))
        {
            return false;
        }

        var age = Clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(issuedMs);
        return age >= TimeSpan.Zero && age <= DeviceLinkPolicy.PageLifetime;
    }

    private static string[] Fields(Guid userId, DeviceLinkRequest request, string issued) =>
        [userId.ToString("N"), request.State, request.CodeChallenge, request.DeviceName, issued];

    private static string[]? Parse(string plain)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(plain);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
