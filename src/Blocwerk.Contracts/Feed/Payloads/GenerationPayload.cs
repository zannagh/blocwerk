// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>panel.changed</c> (and of older <c>wall.updated</c> rows, before <see cref="WallPayload"/>).</summary>
public sealed record GenerationPayload(int Generation) : FeedPayload;
