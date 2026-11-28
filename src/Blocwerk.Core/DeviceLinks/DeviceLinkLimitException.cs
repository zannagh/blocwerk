// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.DeviceLinks;

/// <summary>The user already holds <see cref="DeviceLinkPolicy.MaxLiveCodes"/> unused codes.</summary>
public sealed class DeviceLinkLimitException : Exception
{
    public DeviceLinkLimitException()
        : base("Too many device links started at once. Wait a minute and try again.")
    {
    }
}
