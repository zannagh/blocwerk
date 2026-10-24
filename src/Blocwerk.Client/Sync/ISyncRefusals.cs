// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>Where a wall's sync or the <c>/api/me</c> poll hands a refusal (<see cref="ClientSync"/>).</summary>
public interface ISyncRefusals
{
    /// <summary>Applies the identity matrix to a refusal and stops what it ends.</summary>
    /// <param name="partition">The label the request was made under.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="status">401, 403, 404 or 410.</param>
    /// <param name="reason">The sign-out reason, or null.</param>
    /// <param name="mine">True for a refusal of the marks alone (the wall stays).</param>
    /// <returns>A task that completes once applied.</returns>
    Task RefusedAsync(string partition, Guid wallId, int status, string? reason, bool mine);
}
