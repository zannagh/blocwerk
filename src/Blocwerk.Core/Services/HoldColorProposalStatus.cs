// <copyright file="HoldColorProposalStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>How many of a wall's live holds have no colour, and how many carry an unconfirmed proposal.</summary>
/// <param name="WithoutColor">Live holds without a colour.</param>
/// <param name="Proposed">Live holds whose colour is still the detection's proposal.</param>
public sealed record HoldColorProposalStatus(int WithoutColor, int Proposed);
