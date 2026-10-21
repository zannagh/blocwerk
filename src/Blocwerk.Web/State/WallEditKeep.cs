// <copyright file="WallEditKeep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>Wall-editor decisions an adds-and-moves-only flush left for the user, as kept on the device (WallDetail.DeployFlush).</summary>
public sealed record WallEditKeep(List<Guid> Deleted, List<Guid> Marked);
