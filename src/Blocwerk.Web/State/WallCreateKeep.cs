// <copyright file="WallCreateKeep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>The new-wall form as kept on the device across a deploy (see WallCreate.DeployFlush). The photo is not kept.</summary>
public sealed record WallCreateKeep(string? Name, string? Description, int Angle, bool HasMarkers, double MarkerSizeMm, bool HadPhoto);
