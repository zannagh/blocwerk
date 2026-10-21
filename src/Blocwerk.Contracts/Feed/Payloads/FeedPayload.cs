// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>
/// A stored feed payload (<c>DataJson</c>). Payloads carry ids, enums, flags and generation numbers only, never personal
/// content: no comment text, names, avatars, a person's stars or tick notes. A guard test pins every payload's properties,
/// so adding one is a deliberate, reviewed change.
/// </summary>
public abstract record FeedPayload
{
    /// <summary>Gets the payload version (<see cref="FeedSchema.PayloadVersion"/>).</summary>
    public int Sv { get; init; } = FeedSchema.PayloadVersion;
}
