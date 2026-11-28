// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services;

/// <summary>
/// The replay key a client sends with a write it may retry (an attempt, a comment). It is unique per user, never across
/// users, and never <see cref="Guid.Empty"/>: an empty id would make every retry-less write of a careless client one
/// "replay" of the first.
/// </summary>
public static class ClientRequestIds
{
    /// <summary>The message of the refusal, for a 400 the client never retries.</summary>
    public const string EmptyMessage = "clientRequestId must not be the empty id.";

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> for <see cref="Guid.Empty"/>; null (no key) is fine.</summary>
    public static void EnsureValid(Guid? clientRequestId)
    {
        if (clientRequestId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(nameof(clientRequestId), EmptyMessage);
        }
    }
}
