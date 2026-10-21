// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>wall.updated</c> and of the structural kinds (segment, panel, link, volume).</summary>
public sealed record GenerationPayload(int Generation) : FeedPayload;
