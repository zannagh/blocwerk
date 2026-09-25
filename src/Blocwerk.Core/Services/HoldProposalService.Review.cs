// <copyright file="HoldProposalService.Review.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Proposals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Blocwerk.Core.Services;

/// <summary>Storing and reviewing the proposals.</summary>
public sealed partial class HoldProposalService
{
    private const int CropSidePx = 360;
    private const double CropMinHalfPx = 120;

    /// <inheritdoc />
    public async Task<IReadOnlyList<HoldProposal>> ListAsync(Guid wallId, HoldProposalStatus status = HoldProposalStatus.Pending, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.HoldProposals.AsNoTracking()
            .Where(p => p.WallId == wallId && p.Status == status)
            .OrderByDescending(p => p.Views).ThenByDescending(p => p.Confidence)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Hold> AcceptAsync(Guid wallId, Guid proposalId, string? color, HoldCategory category, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var p = await PendingAsync(db, wallId, proposalId, ct);
        if (p.PanelId is not { } panelId || p.PanelX is not { } x || p.PanelY is not { } y || p.PanelRadius is not { } r)
        {
            throw new UserFacingException("This hold is only seen in 3D, not on a panel photo. Add it on the panel by hand.");
        }

        // The normal hold-creation path: panel truth, editor check, activity log, generation stamp.
        var hold = await wallService.AddHoldAsync(wallId, x, y, r, color, category, wallPanelId: panelId);
        await MarkAsync(db, p, HoldProposalStatus.Accepted, hold.Id, ct);
        logger.LogInformation("Hold proposal {ProposalId} on wall {WallId} accepted as hold {HoldId}", p.Id, wallId, hold.Id);
        return hold;
    }

    /// <inheritdoc />
    public async Task RejectAsync(Guid wallId, Guid proposalId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        await MarkAsync(db, await PendingAsync(db, wallId, proposalId, ct), HoldProposalStatus.Rejected, null, ct);
    }

    /// <inheritdoc />
    public async Task<byte[]?> CropAsync(Guid wallId, Guid proposalId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var p = await db.HoldProposals.AsNoTracking().FirstOrDefaultAsync(x => x.Id == proposalId && x.WallId == wallId, ct);
        if (p is null || files is null)
        {
            return null;
        }

        var photos = await CapturePhotosAsync(db, p.GeometryModelId, ct);
        var bytes = photos.TryGetValue(p.BestPhoto, out var path) ? await files.ReadAsync(path, ct) : null;
        return bytes is null ? null : Crop(bytes, p.BestPx, p.BestPy, p.BestRadiusPx);
    }

    /// <summary>Replaces the wall's pending proposals with <paramref name="candidates"/>; returns how many map onto a panel.</summary>
    private static async Task<int> ReplacePendingAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, List<HoldProposalCandidate> candidates, ProposalInputs inputs, CancellationToken ct)
    {
        await db.HoldProposals.Where(p => p.WallId == wallId && p.Status == HoldProposalStatus.Pending).ExecuteDeleteAsync(ct);
        var frames = inputs.Facets.ToDictionary(f => f.Id, f => f.Frame, StringComparer.Ordinal);
        var onPanels = 0;
        foreach (var c in candidates)
        {
            var panel = PanelPointMapper.Map(c.World, c.SizeMm, frames[c.FacetId], c.FacetId, inputs.Panels);
            onPanels += panel is null ? 0 : 1;
            db.HoldProposals.Add(new HoldProposal
            {
                WallId = wallId, GeometryModelId = modelId, FacetId = c.FacetId, A = c.A, B = c.B, H = c.H,
                X = Math.Round(c.World[0], 1), Y = Math.Round(c.World[1], 1), Z = Math.Round(c.World[2], 1),
                SizeMm = c.SizeMm, Views = c.Views, Confidence = c.Confidence,
                BestPhoto = c.Best.Photo, BestPx = Math.Round(c.Best.Px, 1), BestPy = Math.Round(c.Best.Py, 1), BestRadiusPx = Math.Round(c.Best.RadiusPx, 1),
                PanelId = panel?.PanelId, PanelX = panel?.X, PanelY = panel?.Y, PanelRadius = panel?.Radius,
            });
        }

        await db.SaveChangesAsync(ct);
        return onPanels;
    }

    private static async Task<HoldProposal> PendingAsync(BlocwerkDbContext db, Guid wallId, Guid proposalId, CancellationToken ct) =>
        await db.HoldProposals.FirstOrDefaultAsync(p => p.Id == proposalId && p.WallId == wallId, ct) is { Status: HoldProposalStatus.Pending } p
            ? p
            : throw new UserFacingException("That proposal does not exist or was already reviewed.");

    private async Task MarkAsync(BlocwerkDbContext db, HoldProposal p, HoldProposalStatus status, Guid? holdId, CancellationToken ct)
    {
        p.Status = status;
        p.HoldId = holdId;
        p.ReviewedAt = DateTimeOffset.UtcNow;
        p.ReviewedByUserId = (await currentUserService.GetCurrentUserAsync()).Id;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A square crop around (x, y) with a ring of the detected radius on the hold, scaled to <see cref="CropSidePx"/>; JPEG.</summary>
    private static byte[]? Crop(byte[] image, double x, double y, double radius)
    {
        var half = Math.Max(CropMinHalfPx, 3 * radius);
        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null)
        {
            return null;
        }

        using var surface = SKSurface.Create(new SKImageInfo(CropSidePx, CropSidePx));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);
        var src = new SKRect((float)(x - half), (float)(y - half), (float)(x + half), (float)(y + half));
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, src, new SKRect(0, 0, CropSidePx, CropSidePx), paint);
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = SKColors.Magenta };
        canvas.DrawCircle(CropSidePx / 2f, CropSidePx / 2f, (float)Math.Max(10, radius * CropSidePx / (2 * half)), ring);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }
}
