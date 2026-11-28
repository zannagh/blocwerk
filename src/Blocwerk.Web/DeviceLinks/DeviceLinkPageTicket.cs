// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.DeviceLinks;
using Microsoft.AspNetCore.DataProtection;

namespace Blocwerk.Web.DeviceLinks;

/// <summary>
/// A protected stamp the consent page puts in its form: who opened it, for which challenge, and when. Link makes a code
/// only while the stamp is younger than <see cref="DeviceLinkPolicy.PageLifetime"/>, so a page left open (or one a
/// stranger's link opened long ago) has to be opened again, with its match code, before it links anything.
/// </summary>
public sealed class DeviceLinkPageTicket(IDataProtectionProvider protection, TimeProvider? clock = null)
{
    private const string Purpose = "Blocwerk.DeviceLink.PageTicket.v1";

    private readonly IDataProtector protector = protection.CreateProtector(Purpose);

    private TimeProvider Clock => clock ?? TimeProvider.System;

    /// <summary>A stamp for <paramref name="userId"/> opening the page for <paramref name="challenge"/> now.</summary>
    public string Issue(Guid userId, string challenge)
    {
        var issued = Clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return protector.Protect($"{userId:N}|{challenge}|{issued}");
    }

    /// <summary>True when the stamp is genuine, names this user and challenge, and is still fresh.</summary>
    public bool IsValid(string? ticket, Guid userId, string challenge)
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

        var parts = plain.Split('|');
        if (parts.Length != 3
            || parts[0] != userId.ToString("N")
            || !string.Equals(parts[1], challenge, StringComparison.Ordinal)
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var issuedMs))
        {
            return false;
        }

        var age = Clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(issuedMs);
        return age >= TimeSpan.Zero && age <= DeviceLinkPolicy.PageLifetime;
    }
}
