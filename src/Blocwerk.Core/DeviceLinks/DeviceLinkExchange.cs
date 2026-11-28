// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.DeviceLinks;

/// <summary>A won exchange: the new personal key, its full token (shown this once) and its name.</summary>
public sealed record DeviceLinkExchange(Guid KeyId, string Token, string Name);
