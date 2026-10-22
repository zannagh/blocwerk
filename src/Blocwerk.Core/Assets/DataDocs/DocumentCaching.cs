// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.Partition;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>Whether a document response may be kept, decided as <see cref="Documents.View3DDocumentService"/> decides it.</summary>
public static class DocumentCaching
{
    /// <summary>
    /// True for a kiosk session that may not keep a cache of the wall (<see cref="KioskCacheRules.AllowsKioskCache"/>):
    /// the response is then <c>no-store</c>, like the <c>/a/</c> routes' and view3d's kiosk answers. The kiosk context
    /// must be initialised (the audience gate has done so).
    /// </summary>
    public static bool NoStore(IKioskContext? kioskContext, Guid wallId) =>
        kioskContext is { IsKiosk: true } && !KioskCacheRules.AllowsKioskCache(kioskContext, wallId);
}
