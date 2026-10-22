// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Contracts.Documents;

/// <summary>The data documents' wire format: their version and the one serializer setting server and client share.</summary>
public static class DataDocumentSchema
{
    /// <summary>The schema version every document carries in its <c>v</c>.</summary>
    public const int Version = 1;

    /// <summary>
    /// Gets the serializer options: web casing, nulls left out. Also the canonical form a document's sha is taken over, so
    /// the property order of the records is part of the contract.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
