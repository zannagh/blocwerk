// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Client.ThreeD;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Contracts.Mine;

namespace Blocwerk.Client.Store;

/// <summary>
/// The client's JSON (source-generated, so trimming keeps every member): the data documents as the server writes them
/// (<see cref="DataDocumentSchema.Options"/>: web defaults, nulls left out), client-store.js's answers, and the feed's
/// boulder payload (<c>FeedJson</c>: web defaults too), and the 3D view's status and mount request (PR 3.13).
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(WallDocument))]
[JsonSerializable(typeof(HoldsDocument))]
[JsonSerializable(typeof(BouldersDocument))]
[JsonSerializable(typeof(GalleryDocument))]
[JsonSerializable(typeof(CommentsDocument))]
[JsonSerializable(typeof(MineDocument))]
[JsonSerializable(typeof(StoreSnapshot))]
[JsonSerializable(typeof(StoreDocs))]
[JsonSerializable(typeof(BoulderPayload))]
[JsonSerializable(typeof(Client3DStatus))]
[JsonSerializable(typeof(Client3DRequest))]
internal sealed partial class ClientJsonContext : JsonSerializerContext;
