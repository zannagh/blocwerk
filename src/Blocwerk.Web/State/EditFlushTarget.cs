// <copyright file="EditFlushTarget.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>Which editor a flush handler belongs to, for the log line when it cannot be saved.</summary>
public sealed record EditFlushTarget(string Editor, Guid? WallId, Guid? UserId);
