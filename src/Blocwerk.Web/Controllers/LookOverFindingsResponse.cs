// <copyright file="LookOverFindingsResponse.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.LookOver;

namespace Blocwerk.Web.Controllers;

/// <summary>What <c>GET …/look-over/findings</c> answers.</summary>
/// <param name="State">The wall's look-over flag and counts.</param>
/// <param name="Findings">The findings, the ones that touch boulders first.</param>
public sealed record LookOverFindingsResponse(WallLookOverState State, IReadOnlyList<LookOverFindingView> Findings);
