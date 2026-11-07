// <copyright file="WallBigUpdateService.StagePanel.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>The recognition half of staging one panel: detection, then enrichment, under one gate slot.</summary>
public partial class WallBigUpdateService
{
    /// <summary>Detects one staged panel's holds, adds them at <paramref name="stagedGen"/> and enriches them.</summary>
    private async Task<List<Hold>> DetectAndEnrichPanelAsync(BlocwerkDbContext db, Wall wall, WallPanel panel, byte[] image, int stagedGen)
    {
        var detected = await holdDetectionService.DetectHoldsAsync(image);
        var staged = new List<Hold>(detected.Count);
        foreach (var d in detected)
        {
            staged.Add(new Hold
            {
                WallId = wall.Id,
                WallPanelId = panel.Id,
                X = d.X,
                Y = d.Y,
                Radius = d.Radius,
                DetectedBox = HoldDetectedBox.Of(d),
                Color = d.Color,
                Confidence = d.Confidence,
                IsAutoDetected = true,
                NeedsReview = true,
                Generation = stagedGen,
            });
        }

        db.Holds.AddRange(staged);
        await holdEnrichment.EnrichSafelyAsync(
            db, new HoldEnrichmentRequest(image, wall, staged, panel.Id, stagedGen, FromStagedPhoto: true), logger);
        return staged;
    }
}
