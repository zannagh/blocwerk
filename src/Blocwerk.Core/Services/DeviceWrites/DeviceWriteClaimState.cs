// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.DeviceWrites;

/// <summary>What taking a replay key for a companion-app write found.</summary>
public enum DeviceWriteClaimState
{
    /// <summary>The key is new (or there is none): run the write, then complete or release the receipt.</summary>
    Fresh,

    /// <summary>The same write already ran: answer with its stored row, write nothing.</summary>
    Replay,

    /// <summary>The same write is running right now in another request: retry later.</summary>
    InFlight,

    /// <summary>The key was used for another kind of write or another wall or boulder: a permanent 400.</summary>
    Mismatch,
}
