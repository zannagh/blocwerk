// <copyright file="HybridZoomService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry.HybridZoom;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>
/// The close-up behind the hybrid zoom (see <see cref="IHybridZoomService"/>): the view's key (panel photo stamp,
/// active model, snapped rectangle, size) is checked against the caller's validator and the cache first; only then
/// are the inputs loaded, the photo chosen (<see cref="HybridZoomPlanner"/>) and rendered
/// (<see cref="HybridZoomRenderer"/>) under the render gate.
/// </summary>
/// <param name="dbContextFactory">The contexts.</param>
/// <param name="files">The capture file store.</param>
/// <param name="options">The options.</param>
/// <param name="cache">Results and the render gate.</param>
/// <param name="logger">The log.</param>
public sealed partial class HybridZoomService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICaptureFileStore files,
    HybridZoomOptions options,
    HybridZoomCache cache,
    ILogger<HybridZoomService> logger) : IHybridZoomService
{
    /// <summary>Requested sizes are rounded up to this step, so nearby sizes share a render.</summary>
    private const int SizeStep = 256;

    /// <summary>How long a request waits for a free render slot before it is told "busy".</summary>
    private static readonly TimeSpan GateWait = TimeSpan.FromSeconds(3);

    /// <inheritdoc />
    public async Task<CloseUpResult> RenderAsync(
        Guid wallId, Guid panelId, ViewRect rect, int maxPx, double? aspect, string? ifNoneMatch, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var panel = await LoadPanelAsync(db, wallId, panelId, ct);
        if (panel is null)
        {
            return new CloseUpResult(CloseUpStatus.NotFound);
        }

        if (await ActiveModelIdAsync(db, wallId, ct) is not { } modelId)
        {
            return new CloseUpResult(CloseUpStatus.NoModel);
        }

        var view = rect.Snapped();
        var px = Math.Clamp(((Math.Max(1, maxPx) + SizeStep - 1) / SizeStep) * SizeStep, SizeStep, Math.Max(SizeStep, options.MaxOutputPx));
        var shape = aspect is { } a && double.IsFinite(a) && a > 0 ? Math.Round(Math.Clamp(a, 0.05, 20), 2) : (double?)null;
        var key = Key(panel, modelId, view, px, shape);
        var etag = ETagOf(key);
        if (cache.TryGet(key, out var cached))
        {
            return string.Equals(ifNoneMatch, etag, StringComparison.Ordinal) && cached.Status == CloseUpStatus.Rendered
                ? cached with { Image = null }
                : cached;
        }

        var result = await ComputeAsync(db, wallId, panel, modelId, view, px, shape, etag, ct);
        cache.Put(key, result);
        return result;
    }

    private async Task<CloseUpResult> ComputeAsync(
        BlocwerkDbContext db, Guid wallId, ZoomPanel panel, Guid modelId, ViewRect view, int px, double? aspect, string etag, CancellationToken ct)
    {
        var (inputs, loaded) = await LoadInputsAsync(db, wallId, panel, modelId, options.CaptureFreshness(), ct);
        if (inputs is null)
        {
            return new CloseUpResult(loaded);
        }

        var (status, plan, volume) = HybridZoomPlanner.Plan(inputs, panel.Id, view, options.Rules(), options.VolumeShare);
        if (plan is null)
        {
            return new CloseUpResult(status, view, Volume: volume);
        }

        if (!await cache.Gate.WaitAsync(GateWait, ct))
        {
            return new CloseUpResult(CloseUpStatus.Busy);
        }

        try
        {
            var shape = aspect ?? (plan.Rect.W * inputs.PanelInfo.Width) / (plan.Rect.H * inputs.PanelInfo.Height);
            var size = HybridZoomRenderer.OutputSize(plan.Choice, px, shape);
            var source = await files.ReadAsync(plan.StoredPath, ct);
            var panelImage = options.ColorMatch ? await PanelImageAsync(db, panel, ct) : null;
            var image = source is null
                ? null
                : await Task.Run(() => HybridZoomRenderer.Render(source, plan, size, options.JpegQuality, panelImage), ct);
            if (image is null)
            {
                logger.LogWarning("Hybrid zoom on wall {WallId}: capture photo {Photo} could not be rendered", wallId, plan.Choice.Name);
                return new CloseUpResult(CloseUpStatus.NoBetterPhoto, view);
            }

            logger.LogDebug(
                "Hybrid zoom on wall {WallId}, panel {PanelId}: {Photo} at {Gain:0.0}x, frontality {Front:0.00}, ΔE {Before:0.0} → {After:0.0}",
                wallId, panel.Id, plan.Choice.Name, plan.Choice.Gain, plan.Choice.Frontality, image.Color?.DeltaEBefore, image.Color?.DeltaEAfter);
            return new CloseUpResult(
                CloseUpStatus.Rendered, plan.Rect, image.Image, etag, plan.Choice.Name, plan.Choice.Gain, volume, HybridZoomRenderer.ContentTypeOf(plan))
            {
                DeltaE = image.Color is { } c ? (c.DeltaEBefore, c.DeltaEAfter) : null,
                CastDeltaE = image.Color is { } k ? (k.CastBefore, k.CastAfter) : null,
            };
        }
        finally
        {
            cache.Gate.Release();
        }
    }

    private static string Key(ZoomPanel panel, Guid modelId, ViewRect view, int px, double? aspect)
    {
        var g = ViewRect.Grid;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{panel.Id:N}:{panel.Generation}:{panel.Revision}:{modelId:N}:{Math.Round(view.X * g)}:{Math.Round(view.Y * g)}:{Math.Round(view.W * g)}:{Math.Round(view.H * g)}:{px}:{aspect}");
    }

    private static string ETagOf(string key) =>
        "\"hz-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20] + "\"";
}
