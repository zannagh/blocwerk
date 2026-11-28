// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Cryptography;

namespace Blocwerk.Core.Services;

/// <summary>A new wall token (invite or public share): 128 random bits as lowercase hex, as the tokens always were.</summary>
internal static class WallTokens
{
    public static string Create() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
