// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Partition;

/// <summary>
/// The secret the partition labels are keyed with (design §6.6): a DataProtection-protected random key of its own, not
/// the BLOCWERK key, so a label names an identity without revealing it and cannot be computed outside the server.
/// </summary>
public interface ICachePartitionKey
{
    /// <summary>Gets the 32-byte HMAC key. Stable across restarts wherever the key file persists.</summary>
    byte[] Key { get; }
}
