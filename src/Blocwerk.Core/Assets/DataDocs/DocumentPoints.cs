// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>Outlines in their document form.</summary>
internal static class DocumentPoints
{
    public static List<DocumentPoint>? From(IEnumerable<ShapePoint>? points) =>
        points?.Select(p => new DocumentPoint(p.Dx, p.Dy)).ToList();

    public static List<IReadOnlyList<DocumentPoint>>? Rings(IEnumerable<IEnumerable<ShapePoint>>? rings) =>
        rings?.Select(r => (IReadOnlyList<DocumentPoint>)From(r)!).ToList();
}
