// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>An event's payload version (<c>sv</c>): one this client does not know may mean anything.</summary>
internal static class SyncPayload
{
    /// <summary>True when the payload names a version this client does not understand: the wall reloads instead.</summary>
    public static bool IsUnknown(FeedEnvelope envelope) =>
        envelope.Data is { ValueKind: JsonValueKind.Object } data
        && data.TryGetProperty("sv", out var sv)
        && (!sv.TryGetInt32(out var version) || !FeedSchema.IsSupported(version));
}
