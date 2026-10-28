// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Documents;

namespace Blocwerk.Client.Store;

/// <summary>Parses a cached data document of a known kind, schema and wall; anything else is no document.</summary>
public static class DataDocumentReader
{
    /// <summary>The document, or null when it does not parse, is of another schema version or names another wall.</summary>
    /// <param name="kind">The kind (<see cref="DataDocumentKinds"/>).</param>
    /// <param name="json">The JSON.</param>
    /// <param name="wallId">The wall it must belong to.</param>
    /// <returns>A <see cref="WallDocument"/>, <see cref="HoldsDocument"/>, <see cref="BouldersDocument"/>,
    /// <see cref="GalleryDocument"/> or <see cref="CommentsDocument"/>, or null.</returns>
    public static object? Read(string kind, string json, Guid wallId)
    {
        try
        {
            var context = ClientJsonContext.Default;
            (int V, Guid WallId, object Doc)? parsed = kind switch
            {
                DataDocumentKinds.Wall => JsonSerializer.Deserialize(json, context.WallDocument) is { } d ? (d.V, d.WallId, d) : null,
                DataDocumentKinds.Holds => JsonSerializer.Deserialize(json, context.HoldsDocument) is { } d ? (d.V, d.WallId, d) : null,
                DataDocumentKinds.Boulders => JsonSerializer.Deserialize(json, context.BouldersDocument) is { } d ? (d.V, d.WallId, d) : null,
                DataDocumentKinds.Gallery => JsonSerializer.Deserialize(json, context.GalleryDocument) is { } d ? (d.V, d.WallId, d) : null,
                DataDocumentKinds.Comments => JsonSerializer.Deserialize(json, context.CommentsDocument) is { } d ? (d.V, d.WallId, d) : null,
                _ => null,
            };
            return parsed is { } p && p.V == DataDocumentSchema.Version && p.WallId == wallId && Complete(p.Doc) ? p.Doc : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    // Every list a document carries is present: a truncated or hand-made body is no document.
    private static bool Complete(object document) => document switch
    {
        WallDocument d => d.Segments is not null && d.Panels is not null,
        HoldsDocument d => d.Holds is not null,
        BouldersDocument d => d.Boulders is not null && d.Boulders.All(b => b.Holds is not null && b.Stats is not null),
        GalleryDocument d => d.Images is not null,
        CommentsDocument d => d.Comments is not null && d.Comments.All(c => c.Author is not null && c.Text is not null),
        _ => false,
    };
}
