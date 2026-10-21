// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed.Payloads;

/// <summary>Payload of <c>hold.changed</c> and <c>hold.deleted</c>.</summary>
public sealed record HoldPayload(int Generation, Guid? PanelId) : FeedPayload;
