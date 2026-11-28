// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services.DeviceWrites;

/// <summary>
/// The replay keys of the companion app's creating writes (<see cref="DeviceWriteReceipt"/>): take the key before the
/// write, complete it with the row made, release it when the write failed. Matched on (user, client request id) only,
/// so another user's write with the same id is their own new write.
/// </summary>
/// <remarks>
/// A device write runs under a hard deadline of <see cref="RequestTimeout"/>. A receipt left without a row for longer
/// than <see cref="AbandonedAfter"/>, past that deadline, belongs to a request that died between the two steps, so the
/// next retry takes it over and a crash never blocks a key for good. <see cref="SweepAsync"/> drops receipts older than
/// <see cref="Retention"/>.
/// </remarks>
public sealed class DeviceWriteReceiptService(IDbContextFactory<BlocwerkDbContext> dbContextFactory, TimeProvider? time = null)
{
    /// <summary>The deadline every device write that takes a receipt runs under (a 1 GiB clip at about 5 Mbit/s).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(30);

    /// <summary>How long an unfinished receipt blocks its key before a retry may take it over: past the deadline.</summary>
    public static readonly TimeSpan AbandonedAfter = RequestTimeout + TimeSpan.FromMinutes(5);

    /// <summary>How long a receipt is kept: an app queue retries for days at most, never a month.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <summary>
    /// Takes <paramref name="clientRequestId"/> for a <paramref name="kind"/> write on <paramref name="scopeId"/>. Null
    /// (no replay key) is always <see cref="DeviceWriteClaimState.Fresh"/> and leaves nothing behind.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The key is the empty id.</exception>
    public async Task<DeviceWriteClaim> ClaimAsync(Guid userId, Guid? clientRequestId, string kind, Guid scopeId, CancellationToken ct)
    {
        ClientRequestIds.EnsureValid(clientRequestId);
        if (clientRequestId is not { } key)
        {
            return DeviceWriteClaim.Fresh;
        }

        if (await TryInsertAsync(userId, key, kind, scopeId, ct))
        {
            return DeviceWriteClaim.Fresh;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var held = await db.DeviceWriteReceipts.FirstOrDefaultAsync(r => r.UserId == userId && r.ClientRequestId == key, ct);
        if (held is null)
        {
            // Released between our insert and this read: the earlier write failed, so this one may run.
            return await TryInsertAsync(userId, key, kind, scopeId, ct) ? DeviceWriteClaim.Fresh : new(DeviceWriteClaimState.InFlight);
        }

        if (held.Kind != kind || held.ScopeId != scopeId)
        {
            return new(DeviceWriteClaimState.Mismatch);
        }

        if (held.SubjectId is { } subject)
        {
            return new(DeviceWriteClaimState.Replay, subject);
        }

        if (clock.GetUtcNow() - held.CreatedAt < AbandonedAfter)
        {
            return new(DeviceWriteClaimState.InFlight);
        }

        // Take the abandoned receipt over with one conditional update, so of two retries exactly one wins.
        var taken = await db.DeviceWriteReceipts
            .Where(r => r.Id == held.Id && r.SubjectId == null && r.CreatedAt == held.CreatedAt)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, clock.GetUtcNow()), ct);
        return taken == 1 ? DeviceWriteClaim.Fresh : new(DeviceWriteClaimState.InFlight);
    }

    /// <summary>Records the row the write made under its key; nothing without a key.</summary>
    public async Task CompleteAsync(Guid userId, Guid? clientRequestId, Guid subjectId, CancellationToken ct)
    {
        if (clientRequestId is not { } key)
        {
            return;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await db.DeviceWriteReceipts
            .Where(r => r.UserId == userId && r.ClientRequestId == key)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SubjectId, subjectId), ct);
    }

    /// <summary>Drops the key of a write that failed, so its retry runs again; nothing without a key.</summary>
    public async Task ReleaseAsync(Guid userId, Guid? clientRequestId)
    {
        if (clientRequestId is not { } key)
        {
            return;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync();
        await db.DeviceWriteReceipts
            .Where(r => r.UserId == userId && r.ClientRequestId == key && r.SubjectId == null)
            .ExecuteDeleteAsync();
    }

    /// <summary>Drops the receipts older than <see cref="Retention"/>; returns how many.</summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var before = clock.GetUtcNow() - Retention;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.DeviceWriteReceipts.Where(r => r.CreatedAt < before).ExecuteDeleteAsync(ct);
    }

    private async Task<bool> TryInsertAsync(Guid userId, Guid key, string kind, Guid scopeId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.DeviceWriteReceipts.Add(new DeviceWriteReceipt
        {
            UserId = userId,
            ClientRequestId = key,
            Kind = kind,
            ScopeId = scopeId,
            CreatedAt = clock.GetUtcNow(),
        });
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (DatabaseErrors.IsUniqueViolation(ex))
        {
            return false;
        }
    }
}
