// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;

namespace Blocwerk.Client.Sync;

/// <summary>
/// Refetches the signed-in viewer's marks of one wall (<c>/api/me/walls/{w}/mine</c>, <c>u-</c> only) into the
/// ledger and the store. Own ratings are not offline, so nothing here reads them.
/// </summary>
internal static class MineRefresh
{
    /// <summary>Refetches; true when the marks are current (or the label holds none), false when they did not arrive.</summary>
    public static async Task<bool> RunAsync(ClientStore store, ISyncTransport transport, ISyncRefusals refusals, string partition, Guid wallId)
    {
        if (!SyncLabels.IsUser(partition))
        {
            return true;
        }

        var answer = await transport.RefreshMineAsync(partition, wallId);
        if (SyncLabels.IsRefusal(answer.Status))
        {
            await refusals.RefusedAsync(partition, wallId, answer.Status, answer.Reason, mine: !SyncLabels.EndsSession(answer.Status));
            return !SyncLabels.EndsSession(answer.Status);
        }

        if (answer.Status == 200 && SyncJson.Read(answer.Body, ClientJsonContext.Default.MineDocument) is { } document)
        {
            store.ReplaceMine(partition, document);
            return true;
        }

        return false;
    }
}
