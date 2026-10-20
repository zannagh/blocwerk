// <copyright file="IWallEditPresence.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Core's seam to the web host's edit leases: whether somebody has a wall's hold editor open right now. Background work
/// that rewrites many holds waits while it is, so it never saves under an editor's unsaved changes. Optional where it
/// is injected — without it (tests, tools) nobody counts as editing.
/// </summary>
public interface IWallEditPresence
{
    /// <summary>True while an unexpired hold-editing lease exists on the wall.</summary>
    /// <param name="wallId">The wall.</param>
    /// <returns>Whether someone is editing it.</returns>
    bool IsBeingEdited(Guid wallId);
}
