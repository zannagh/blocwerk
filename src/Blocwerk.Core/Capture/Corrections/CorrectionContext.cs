// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>An open correction: the admin context (disposed by the caller), the active model and its capture.</summary>
/// <param name="Db">The admin context of the wall.</param>
/// <param name="UserId">The admin.</param>
/// <param name="Model">The wall's active model (not tracked).</param>
/// <param name="Document">Its parsed document.</param>
/// <param name="CaptureId">The capture that points at it, if any.</param>
internal sealed record CorrectionContext(BlocwerkDbContext Db, Guid UserId, WallGeometryModel Model, WallGeometryDocument Document, Guid? CaptureId);
