// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Tests;

/// <summary>A readable stream that will not say how long it is, so an HTTP body built on it goes out chunked.</summary>
internal sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();
}
