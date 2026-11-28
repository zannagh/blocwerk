// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Controllers;

/// <summary>A boulder's variant links as stored: its parent (null when it is no variant) and its own variants.</summary>
/// <param name="BoulderId">The boulder.</param>
/// <param name="ParentId">The boulder it is a variant of, or null.</param>
/// <param name="VariantIds">The boulders that are its variants, ordered by id.</param>
public sealed record DeviceBoulderLinks(Guid BoulderId, Guid? ParentId, IReadOnlyList<Guid> VariantIds);
