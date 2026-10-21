// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>member.changed</c>.</summary>
public sealed record MemberPayload(string Role) : FeedPayload;
