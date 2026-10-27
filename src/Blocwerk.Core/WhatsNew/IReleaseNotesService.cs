// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.WhatsNew;

/// <summary>Blocwerk's own release notes, read from its public GitHub repository and cached.</summary>
public interface IReleaseNotesService
{
    /// <summary>
    /// The releases, newest first. Never throws and never waits longer than the configured timeout:
    /// a GitHub failure answers the last good list, or an unavailable feed when there is none.
    /// </summary>
    Task<ReleaseFeed> GetAsync(CancellationToken cancellationToken = default);
}
