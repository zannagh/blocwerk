// <copyright file="ReviseMark.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>One of the boulder's own holds as stored, the input to <see cref="RevisePrefillMapper"/>.</summary>
/// <param name="HoldId">The hold row the boulder points at (possibly from an older generation).</param>
/// <param name="Type">The boulder's mark on it.</param>
/// <param name="Usage">The boulder's usage rule for it.</param>
/// <param name="PanelId">The panel that row was drawn on; null for a legacy or virtual hold (the centre photo).</param>
/// <param name="X">Panel-local X of that row, 0..1.</param>
/// <param name="Y">Panel-local Y of that row, 0..1.</param>
public record ReviseMark(Guid HoldId, HoldType Type, HoldUsage Usage, Guid? PanelId, double X, double Y);
