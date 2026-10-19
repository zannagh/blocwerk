// <copyright file="StagedHoldIsTwinException.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// Thrown when a staged hold is deleted with <see cref="Enums.StagedTwinDeletion.Refuse"/> while an old hold's carry
/// verdict names it as that old hold's new position. Nothing was deleted; the caller asks the user and deletes again
/// with <see cref="Enums.StagedTwinDeletion.ReleaseTwin"/>.
/// </summary>
public class StagedHoldIsTwinException : UserFacingException
{
    public StagedHoldIsTwinException(Guid holdId, IReadOnlyList<Guid> oldHoldIds)
        : base(oldHoldIds.Count == 1
            ? "This hold is where an old hold was matched to. Deleting it keeps the old hold's choice but carries it at its old place."
            : $"This hold is where {oldHoldIds.Count} old holds were matched to. Deleting it keeps their choices but carries them at their old places.")
    {
        HoldId = holdId;
        OldHoldIds = oldHoldIds;
    }

    /// <summary>The staged hold that was not deleted.</summary>
    public Guid HoldId { get; }

    /// <summary>The old holds whose verdicts name it.</summary>
    public IReadOnlyList<Guid> OldHoldIds { get; }
}
