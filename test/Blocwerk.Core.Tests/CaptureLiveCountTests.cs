// <copyright file="CaptureLiveCountTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The capture history's "N holds placed on the 3D model" and "N volumes found, M holds placed on them" lines count what
/// the wall shows now for a capture of the active model: hidden or removed volumes, stale volume placements and holds no
/// longer placed from photos drop out, while a capture of an older model keeps its stored text.
/// </summary>
public class CaptureLiveCountTests
{
    [Fact]
    public async Task HiddenVolumesAndStalePlacements_DropOutOfTheVolumeLine()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, modelId) = await SeedAsync(h, placed: 0, volumes: 3, onVolumes: 5);
        var visible = await VolumeAsync(h, modelId);
        var hidden = await VolumeAsync(h, modelId, v => v.IsHidden = true);
        await VolumeAsync(h, modelId, v => v.IsRemoved = true);
        await HoldAsync(h, volumeId: visible);
        await HoldAsync(h, volumeId: visible);
        await HoldAsync(h, volumeId: visible, fromA: 900);
        await HoldAsync(h, volumeId: hidden);

        var capture = Assert.Single(await s.Service.GetCapturesAsync(h.WallId));

        Assert.Equal(captureId, capture.Id);
        Assert.Equal("1 volume found, 2 holds placed on it.", capture.FollowUp);
    }

    [Fact]
    public async Task HoldsPlacedFromPhotos_AreCountedLive()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, _) = await SeedAsync(h, placed: 856, volumes: 0, onVolumes: 0);
        await HoldAsync(h, source: HoldMetric.TextureRegistration);
        await HoldAsync(h, source: HoldMetric.TextureRegistrationCarried);
        await HoldAsync(h, source: HoldMetric.TextureRegistrationRejected);
        await HoldAsync(h, source: HoldMetric.LocalMarker);

        Assert.Equal(
            "2 holds placed on the 3D model (1 kept from the previous model). 1 hold left unmeasured.",
            (await s.Service.GetCaptureAsync(captureId))!.FollowUp);
    }

    [Fact]
    public async Task ACaptureOfAnOlderModel_KeepsItsStoredText()
    {
        using var h = new WallTestHarness();
        using var s = new CaptureScenario(h);
        var (captureId, modelId) = await SeedAsync(h, placed: 856, volumes: 3, onVolumes: 5);
        await using (var db = h.CreateContext())
        {
            (await db.WallGeometryModels.SingleAsync(m => m.Id == modelId)).IsActive = false;
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            "856 holds placed on the 3D model, 3 volumes found, 5 holds placed on them.",
            (await s.Service.GetCaptureAsync(captureId))!.FollowUp);
    }

    [Theory]
    [InlineData(CaptureFollowUpText.KeptFromPreviousVersion, 1, "Holds, shapes and volumes carried over from the previous model version.")]
    [InlineData("2 volumes found (from the sparse points, coarser)", 1, "1 volume found (from the sparse points, coarser).")]
    [InlineData("2 volumes found", 0, "No visible volume left.")]
    [InlineData("", 1, null)]
    public void Summary_ReplacesOnlyCountsTheStepReported(string stored, int volumes, string? expected)
    {
        var record = new CaptureFollowUpRecord([Entry(DetectVolumesFollowUpStep.StepKey, stored)]);

        Assert.Equal(expected, CaptureFollowUpText.Summary(record, new CaptureLiveCounts(Volumes: volumes)));
    }

    private static CaptureFollowUpEntry Entry(string key, string summary) =>
        new(key, CaptureFollowUpOutcome.Done, summary, DateTimeOffset.UtcNow);

    private static async Task<(Guid CaptureId, Guid ModelId)> SeedAsync(WallTestHarness h, int placed, int volumes, int onVolumes)
    {
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        var record = new CaptureFollowUpRecord(
        [
            Entry(PlaceHoldsFollowUpStep.StepKey, placed == 0 ? string.Empty : PlaceHoldsFollowUpStep.Describe(placed, 0)),
            Entry(DetectVolumesFollowUpStep.StepKey, DetectVolumesFollowUpStep.Describe(volumes, onVolumes)),
        ]);
        await using var db = h.CreateContext();
        (await db.WallCaptures.SingleAsync(c => c.Id == captureId)).FollowUpJson = record.ToJson();
        await db.SaveChangesAsync();
        return (captureId, modelId);
    }

    private static async Task<Guid> VolumeAsync(WallTestHarness h, Guid modelId, Action<WallVolume>? edit = null)
    {
        await using var db = h.CreateContext();
        var volume = new WallVolume { WallId = h.WallId, GeometryModelId = modelId, FacetId = "f1", FootprintJson = "[]", SurfaceJson = "{}" };
        edit?.Invoke(volume);
        db.WallVolumes.Add(volume);
        await db.SaveChangesAsync();
        return volume.Id;
    }

    private static async Task HoldAsync(WallTestHarness h, Guid? volumeId = null, double fromA = 500, string? source = null)
    {
        await using var db = h.CreateContext();
        db.Holds.Add(new Hold
        {
            WallId = h.WallId, X = 0.5, Y = 0.5, Radius = 0.02, FacetId = "f1", PlaneAMm = 500, PlaneBMm = 500, MetricSource = source,
            VolumePlacementJson = volumeId is { } v ? new HoldVolumePlacement(v, 500, 500, 40, [0, 0, 1], fromA, 500, "panel").ToJson() : null,
        });
        await db.SaveChangesAsync();
    }
}
