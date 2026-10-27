// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>
/// The sync loop's JSON (source-generated, so trimming keeps every member): the feed's envelopes, catch-up pages and
/// byes (<c>FeedJson</c>: web defaults), and client-sync.js's answers.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeedEnvelope))]
[JsonSerializable(typeof(FeedChanges))]
[JsonSerializable(typeof(FeedStreamBye))]
[JsonSerializable(typeof(SyncResponse))]
[JsonSerializable(typeof(SyncIndex))]
internal sealed partial class SyncJsonContext : JsonSerializerContext;
