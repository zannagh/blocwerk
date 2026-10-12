// <copyright file="BoulderMoveLine.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared;

/// <summary>One line of the notice.</summary>
/// <param name="Label">The hold, or the holds.</param>
/// <param name="Text">What happened to it.</param>
/// <param name="Outcome">The outcome (styles a hold that was taken off).</param>
/// <param name="Source">How it was measured.</param>
internal sealed record BoulderMoveLine(string Label, string Text, HoldMoveOutcome Outcome, HoldMoveSource Source);
