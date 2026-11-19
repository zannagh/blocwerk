// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Projects;

/// <summary>
/// The signed-in user's boulder projects: boulders they marked as something they are working on. Private to the user and
/// modelled on favourites. Only wall members may hold them; anonymous, share-link and kiosk viewers have none.
/// </summary>
public interface IBoulderProjectService
{
    /// <summary>Flips the current user's project mark on a boulder and returns the new state.</summary>
    Task<bool> ToggleProjectAsync(Guid boulderId);

    /// <summary>
    /// Sets the current user's project mark to an absolute value and returns it. Idempotent, so it is the call an offline
    /// queue replays.
    /// </summary>
    Task<bool> SetProjectAsync(Guid boulderId, bool project);

    /// <summary>True when the boulder is one of the current user's projects.</summary>
    Task<bool> IsProjectAsync(Guid boulderId);

    /// <summary>
    /// The current user's projects on a wall: every boulder of the wall they marked (drafts and archived included), as the
    /// boulder list renders them, newest mark first.
    /// </summary>
    Task<List<BoulderListItem>> GetMyProjectsAsync(Guid wallId);
}
