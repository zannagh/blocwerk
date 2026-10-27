// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>What the sync loop may do under a partition label and a status (plan §3, the identity matrix).</summary>
public static class SyncLabels
{
    /// <summary>True for a signed-in label: the only one with marks and an <c>/api/me/changes</c> poll.</summary>
    /// <param name="partition">The label.</param>
    /// <returns>Whether it is <c>u-</c>.</returns>
    public static bool IsUser(string? partition) => partition is not null && partition.StartsWith("u-", StringComparison.Ordinal);

    /// <summary>
    /// True for a label the client syncs: <c>u-</c> and a share link's <c>anon</c>. A kiosk tablet (<c>k-</c>) polls
    /// nothing: it stays on its circuits (plan §3).
    /// </summary>
    /// <param name="partition">The label.</param>
    /// <returns>Whether the client syncs it.</returns>
    public static bool Syncs(string? partition) => IsUser(partition) || partition == "anon";

    /// <summary>True for a refusal the identity matrix acts on: 401, 403, 404 and 410.</summary>
    /// <param name="status">The status.</param>
    /// <returns>Whether it purges or locks something.</returns>
    public static bool IsRefusal(int status) => status is 401 or 403 or 404 or 410;

    /// <summary>True for a refusal that ends the session (401, 410): the whole partition locks or goes.</summary>
    /// <param name="status">The status.</param>
    /// <returns>Whether every wall stops.</returns>
    public static bool EndsSession(int status) => status is 401 or 410;
}
