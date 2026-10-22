// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Builds one data document of one class, always from the database (never from a circuit's cached wall), and returns its
/// canonical bytes (<see cref="DataDocumentSchema.Options"/>): the same rows give the same bytes, so the sha moves only
/// when what the class sees does. The caller has decided the class already; the build depends on the wall, the class and
/// the kind only, which is what lets one build serve every caller of the class.
/// </summary>
public sealed class DataDocumentBuilder(RootDbContextFactory dbContextFactory, IWallImageStorage imageStorage)
{
    /// <summary>The document's bytes and the people it names, or null when the wall is gone.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a data document kind.</exception>
    public async Task<BuiltDataDocument?> BuildAsync(Guid wallId, DocumentAudience audience, string kind, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        if (kind == DataDocumentKinds.Boulders)
        {
            return await BouldersDocumentBuilder.BuildAsync(db, wallId, audience, ct) is var (boulders, people)
                ? new BuiltDataDocument(Serialize(boulders), people)
                : null;
        }

        object? document = kind switch
        {
            DataDocumentKinds.Wall => await WallDocumentBuilder.BuildAsync(db, wallId, ct),
            DataDocumentKinds.Holds => await HoldsDocumentBuilder.BuildAsync(db, wallId, ct),
            DataDocumentKinds.Gallery => await GalleryDocumentBuilder.BuildAsync(db, imageStorage, wallId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a data document kind."),
        };
        return document is null ? null : new BuiltDataDocument(Serialize(document), []);
    }

    private static byte[] Serialize(object document) =>
        JsonSerializer.SerializeToUtf8Bytes(document, document.GetType(), DataDocumentSchema.Options);
}
