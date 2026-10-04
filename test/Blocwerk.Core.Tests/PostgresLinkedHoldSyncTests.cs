// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The linked-hold sync on PostgreSQL: the migration's columns, the batch, its undo and the startup marker.</summary>
[Trait("Db", "Postgres")]
public class PostgresLinkedHoldSyncTests
{
    [PostgresFact]
    public async Task SyncFillsTheKickboardOnPanel2_AndItsUndoRestoresIt()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync(PostgresTestDatabase.Create());
        var main = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.IsOnKickboard = true);
        var panel2 = await f.AddHoldAsync(await f.AddPanelAsync(1, 0), h => h.Color = "red");
        await f.LinkAsync(main, panel2);
        await f.SyncService().SetWinnerPanelAsync(f.WallId, (0, 0));

        var result = await f.SyncService().SyncAsync(f.WallId);

        Assert.True((await f.GetAsync(panel2)).IsOnKickboard);
        Assert.Equal("red", (await f.GetAsync(main)).Color);
        Assert.False((await f.SyncService().SyncAsync(f.WallId)).Report.Any);

        var reverted = await f.SyncService().RevertAsync(f.WallId, result.BatchId!.Value);

        Assert.True(reverted.Reverted, reverted.Error);
        Assert.False((await f.GetAsync(panel2)).IsOnKickboard);
        Assert.Null((await f.GetAsync(main)).Color);
    }

    [PostgresFact]
    public async Task StartupRunsOnce_AndTheMarkerSurvivesAnUndo()
    {
        using var f = await LinkedHoldSyncFixture.CreateAsync(PostgresTestDatabase.Create());
        var main = await f.AddHoldAsync(await f.AddPanelAsync(0, 0), h => h.Category = HoldCategory.Foot);
        var panel2 = await f.AddHoldAsync(await f.AddPanelAsync(1, 0));
        await f.LinkAsync(main, panel2);

        await LinkedHoldSyncStartup.RunIfNeededAsync(f.Journalling, f.Journal, NullLogger.Instance);
        var batch = Assert.Single(await f.BatchesAsync(LinkedHoldSyncStartup.BatchLabel));
        Assert.True((await f.RevertAsync(batch.Id)).Reverted);
        await LinkedHoldSyncStartup.RunIfNeededAsync(f.Journalling, f.Journal, NullLogger.Instance);

        Assert.Equal(HoldCategory.Hand, (await f.GetAsync(panel2)).Category);
        Assert.Equal(LinkedHoldSync.Version, (await f.GetWallAsync()).LinkedHoldSyncVersion);
    }
}
