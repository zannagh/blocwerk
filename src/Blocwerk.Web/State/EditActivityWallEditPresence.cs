// <copyright file="EditActivityWallEditPresence.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Web.State;

/// <summary>
/// <see cref="IWallEditPresence"/> on top of <see cref="EditActivityRegistry"/>: a wall counts as being edited while an
/// unexpired <see cref="EditKind.WallEdit"/> lease names it (the hold editor is open). Leases expire on inactivity, so
/// a forgotten tab stops counting on its own.
/// </summary>
public sealed class EditActivityWallEditPresence(EditActivityRegistry registry) : IWallEditPresence
{
    /// <inheritdoc/>
    public bool IsBeingEdited(Guid wallId) =>
        registry.Snapshot().Any(e => e.EditKind == EditKind.WallEdit && e.WallId == wallId);
}
