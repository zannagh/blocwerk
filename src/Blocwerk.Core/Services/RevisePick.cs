// <copyright file="RevisePick.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>One current hold the Revise editor opens with ticked, with the boulder's own mark for it.</summary>
/// <param name="HoldId">The live hold to tick.</param>
/// <param name="Type">The boulder's mark (start / top / normal), carried over from the old hold.</param>
/// <param name="Usage">The boulder's usage rule for the hold, carried over from the old hold.</param>
/// <param name="Source">How the hold was found, so a guess can be shown as one.</param>
public record RevisePick(Guid HoldId, HoldType Type, HoldUsage Usage, RevisePickSource Source)
{
    /// <summary>The boulder's own hold ids that led here. Several when old holds were merged into this one.</summary>
    public IReadOnlyList<Guid> SourceHoldIds { get; init; } = [];
}
