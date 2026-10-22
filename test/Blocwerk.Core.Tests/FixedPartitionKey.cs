// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Assets.Partition;

namespace Blocwerk.Core.Tests;

/// <summary>A partition key of 32 copies of one byte, so tests can tell two keys apart.</summary>
public sealed class FixedPartitionKey(byte fill) : ICachePartitionKey
{
    public byte[] Key { get; } = Enumerable.Repeat(fill, 32).ToArray();
}
