// <copyright file="InlineEditKeep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>The boulder page's open inline editors, as kept on the device across a deploy (see BoulderDetail.DeployFlush).</summary>
public sealed record InlineEditKeep(bool Meta, string? Name, string? Grade, bool Propose, string? ProposedGrade);
