// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Documents;

/// <summary>A view3d document the caller may read.</summary>
/// <param name="Document">The stored document.</param>
/// <param name="NoStore">
/// The caller is a kiosk session that may not keep a cache of the wall (<see cref="Partition.KioskCacheRules"/>): the
/// response must be <c>no-store</c>, like the <c>/a/</c> routes' kiosk answers.
/// </param>
public sealed record ServedView3DDocument(StoredView3DDocument Document, bool NoStore);
