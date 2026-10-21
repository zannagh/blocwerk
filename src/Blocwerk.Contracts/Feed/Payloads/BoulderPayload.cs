// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>boulder.changed</c> and <c>boulder.deleted</c>; drafts go to setters only.</summary>
public sealed record BoulderPayload(int Generation, bool IsDraft) : FeedPayload;
