// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of the <c>video.*</c> kinds.</summary>
public sealed record VideoPayload(Guid BoulderId, string EncodingStatus, bool HasHls) : FeedPayload;
