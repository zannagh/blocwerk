// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Contracts.Assets;

/// <summary>The manifest's wire format: its version and the one serializer setting the server and clients share.</summary>
public static class ManifestSchema
{
    /// <summary>The manifest schema version (<see cref="WallManifest.V"/>).</summary>
    public const int Version = 1;

    /// <summary>
    /// Gets the serializer options: web casing, nulls left out (except <see cref="ManifestAsset.Sha"/>). Also the canonical
    /// form the wall hash is taken over, so the property order of the records is part of the contract.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
