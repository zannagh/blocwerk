// <copyright file="WallVolumeList.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>Code-behind of the volume list: everything goes through <see cref="IWallVolumeService"/> (wall admins only).</summary>
public partial class WallVolumeList
{
    private Guid loadedWallId;
    private bool loaded;
    private bool busy;
    private bool wallFlat;
    private string? message;
    private string? failure;
    private IReadOnlyList<WallVolumeSummary> volumes = [];

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: nothing is offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IWallVolumeService Volumes { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallVolumeList> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>A volume in a few words: size, height, holds on it.</summary>
    internal static string VolumeText(WallVolumeSummary v) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{v.AreaM2:0.00} m², {v.HeightMm:0} mm high, {v.HoldCount} holds on it{(v.IsHidden ? " (hidden)" : string.Empty)}.");

    /// <summary>Where it is: the facet and its centre on it.</summary>
    internal static string PlaceText(WallVolumeSummary v) =>
        string.Create(CultureInfo.InvariantCulture, $"facet {v.FacetId}, {v.CentreA / 1000:0.00} m across, {v.CentreB / 1000:0.00} m up");

    /// <summary>The flat-sided shape, or null for a height field.</summary>
    internal static string? ShapeText(WallVolumeSummary v) => v.HasFlatSides
        ? string.Create(CultureInfo.InvariantCulture, $"Flat sides: {ShapeName(v.Shape)}, {v.Faces} faces, ±{v.FitRmsMm ?? 0:0} mm vs scan.")
        : null;

    /// <summary>The shape in words; several peaks are likely volumes detected as one.</summary>
    internal static string? ShapeName(string? shape) => shape switch
    {
        "plateau" => "flat top",
        "multi-peak" => "several peaks (possibly several volumes: Remove and re-detect)",
        _ => shape,
    };

    // Loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation between walls.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || Hidden)
        {
            return;
        }

        loadedWallId = WallId;
        message = null;
        failure = null;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            volumes = await Volumes.ListAsync(WallId);
            wallFlat = await Volumes.GetWallFlatSidesAsync(WallId);
            loaded = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load the volumes of wall {WallId}", WallId);
            loaded = false;
        }
    }

    private Task SetFlatAsync(WallVolumeSummary v, ChangeEventArgs e) => RunAsync(async () =>
    {
        var on = e.Value is true;
        var r = await Volumes.SetFlatSidesAsync(WallId, v.Id, on);
        return $"Volume {v.Index} {(on ? "has flat sides now" : "follows the scan again")}; {r.HoldsChanged} holds moved.";
    });

    private Task SetWallFlatAsync(ChangeEventArgs e) => RunAsync(async () =>
    {
        var on = e.Value is true;
        await Volumes.SetWallFlatSidesAsync(WallId, on, applyToAll: false);
        return on ? "Newly found volumes will get flat sides where they fit." : "Newly found volumes follow the scan.";
    });

    private Task ApplyToAllAsync() => RunAsync(async () =>
    {
        var r = await Volumes.SetWallFlatSidesAsync(WallId, wallFlat, applyToAll: true);
        var kept = r.KeptHeightField > 0 ? $" {r.KeptHeightField} are too small or flat for flat sides and follow the scan." : string.Empty;
        return $"{r.FlatSided} volumes have flat sides; {r.HoldsChanged} holds moved.{kept}";
    });

    private Task ToggleHiddenAsync(WallVolumeSummary v) => RunAsync(async () =>
    {
        var r = await Volumes.SetHiddenAsync(WallId, v.Id, !v.IsHidden);
        return $"{r.HoldsPlaced} holds are on the visible volumes now.";
    });

    private Task SetRemovedAsync(WallVolumeSummary v, bool removed) => RunAsync(async () =>
    {
        var r = await Volumes.SetRemovedAsync(WallId, v.Id, removed);
        return removed
            ? $"Volume {v.Index} removed; its holds are back on the wall. It will not be found again."
            : $"Volume {v.Index} restored; {r.HoldsPlaced} holds are on the visible volumes now.";
    });

    private async Task RunAsync(Func<Task<string>> action)
    {
        busy = true;
        failure = null;
        message = null;
        try
        {
            message = await action();
        }
        catch (Exception ex) when (ex is UserFacingException or KioskRestrictedException)
        {
            failure = ex.Message;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Volume action on wall {WallId} failed", WallId);
            failure = UserFacingException.GenericMessage;
        }
        finally
        {
            await ReloadAsync();
            busy = false;
        }
    }
}
