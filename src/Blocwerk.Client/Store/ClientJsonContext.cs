// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Mine;

namespace Blocwerk.Client.Store;

/// <summary>
/// The client's JSON (source-generated, so trimming keeps every member): the data documents as the server writes them
/// (<see cref="DataDocumentSchema.Options"/>: web defaults, nulls left out) and client-store.js's answers.
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
internal sealed partial class ClientJsonContext : JsonSerializerContext;
