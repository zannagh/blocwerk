// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>
/// The feed's two version numbers. Bump them deliberately: old clients ignore unknown kinds and resync a wall when they
/// meet a payload version they do not know.
/// </summary>
public static class FeedSchema
{
    /// <summary>The envelope shape (<see cref="FeedEnvelope.V"/>).</summary>
    public const int EnvelopeVersion = 1;

    /// <summary>The payload version written into every <see cref="Payloads.FeedPayload.Sv"/> and stored as the row's schema version.</summary>
    public const short PayloadVersion = 1;

    /// <summary>True when a client built against this assembly understands payloads of version <paramref name="sv"/>.</summary>
    public static bool IsSupported(int sv) => sv is >= 1 and <= PayloadVersion;
}
