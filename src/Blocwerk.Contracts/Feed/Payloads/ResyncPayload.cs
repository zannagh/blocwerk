// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>wall.resync</c>: why the wall has to be reloaded (an operation label, never user input).</summary>
public sealed record ResyncPayload(string Reason) : FeedPayload;
