// <copyright file="HoldActionOutcome.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What an action that changes the wall model will do to the current pick, in three plain sentences
/// that every such action answers the same way: what happens to the holds, what happens to the
/// boulders using them, and whether it can be taken back.
/// </summary>
/// <param name="Holds">Which hold stays, which disappears, and what changes on the one that stays.</param>
/// <param name="Boulders">How many boulders are affected and what happens to them.</param>
/// <param name="Undo">Whether and where it can be undone.</param>
/// <param name="Danger">True when something is removed or the action cannot be taken back.</param>
public sealed record HoldActionOutcome(string Holds, string Boulders, string Undo, bool Danger)
{
    /// <summary>The three sentences as one line, for a screen reader label.</summary>
    public string Sentence => $"{Holds} {Boulders} {Undo}";
}
