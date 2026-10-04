// <copyright file="ILinkedHoldSyncService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>One panel cell the admin can pick as the winner, numbered as the wall page numbers panels (row, then column).</summary>
/// <param name="Col">The grid column.</param>
/// <param name="Row">The grid row.</param>
/// <param name="Label">"Panel 2 (column 1, row 0)".</param>
public sealed record LinkedHoldPanelOption(int Col, int Row, string Label);

/// <summary>The "Sync linked holds" settings state of one wall.</summary>
/// <param name="Panels">The live panels to choose from.</param>
/// <param name="Winner">The cell that currently wins conflicts (the setting, or the centre panel).</param>
/// <param name="LinkCount">How many cross-panel links the current generation has.</param>
/// <param name="RevertableBatchId">The latest sync that can still be undone, if any.</param>
public sealed record LinkedHoldSyncStatus(
    IReadOnlyList<LinkedHoldPanelOption> Panels, (int Col, int Row)? Winner, int LinkCount, Guid? RevertableBatchId);

/// <summary>The outcome of one admin sync run.</summary>
/// <param name="Report">What changed.</param>
/// <param name="BatchId">The journal batch to undo it with; null when nothing changed.</param>
public sealed record LinkedHoldSyncResult(HoldSyncReport Report, Guid? BatchId);

/// <summary>
/// Keeps the physical properties of linked holds equal across panels (see <see cref="LinkedHoldSyncRules"/>). The admin
/// action syncs a wall's whole current generation in one journal batch, exactly revertable; the winner panel is the
/// per-wall "Panel that wins for linked holds" setting. Wall admin only, never from a kiosk tablet.
/// </summary>
public interface ILinkedHoldSyncService
{
    /// <summary>Reads the settings state.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    Task<LinkedHoldSyncStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Sets the panel that wins conflicts; null goes back to the centre panel.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="cell">A panel cell of this wall, or null.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task SetWinnerPanelAsync(Guid wallId, (int Col, int Row)? cell, CancellationToken ct = default);

    /// <summary>Syncs every linked group of the wall's current generation in one journal batch; takes the hold write lock.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The report and the batch.</returns>
    Task<LinkedHoldSyncResult> SyncAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Reverts a sync exactly; refuses (and changes nothing) when something it touched was edited since.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="batchId">The batch <see cref="SyncAsync"/> returned.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The revert outcome.</returns>
    Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default);
}
