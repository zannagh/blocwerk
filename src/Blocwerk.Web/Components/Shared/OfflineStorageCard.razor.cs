// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Settings → "Offline storage": the device's cache size per wall, the 3D opt-out, the keep-offline pin and the clear.
/// <para>
/// HIDDEN ON A KIOSK. A tablet's partition holds only its bound wall's wall-level assets and manages itself (it is
/// refilled after any wipe and never grows past one wall). Whoever stands at the tablet is a member acting on it, not
/// its owner, and the profile page knows the acting member, not who manages the kiosk. A clear or a pin there would be
/// a device-owner action offered to every member, and a read-only copy would tell a member nothing they can act on.
/// The account pane that hosts this card is not rendered on a kiosk either; the check here is the card's own.
/// </para>
/// </summary>
public partial class OfflineStorageCard
{
    private OfflineStorageFacts? facts;
    private IReadOnlyDictionary<Guid, string> names = new Dictionary<Guid, string>();
    private bool busy;
    private bool confirmingClear;
    private string? error;

    // Bumped after every write, so Blazor recreates the checkboxes bound to the re-read state (a native checkbox left
    // flipped by a failed write is not patched back by a plain diff).
    private int rev;

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private IWallService WallService { get; set; } = default!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        if (KioskContext.IsKiosk)
        {
            return;
        }

        var walls = await WallService.GetMyWallsAsync();
        names = walls.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.First().Name);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && !KioskContext.IsKiosk)
        {
            await ReloadAsync();
            StateHasChanged();
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            facts = await JS.InvokeAsync<OfflineStorageFacts?>("bwAssetReport.report");
        }
        catch (JSException)
        {
            facts = facts ?? new OfflineStorageFacts(false, false, false, true, false, false, false, null, false, 0, []);
        }

        rev++;
    }

    private Task OnKeep3dChange(ChangeEventArgs e) =>
        WriteAsync("bwAssetCache.setKeep3d", e.Value is true);

    private Task OnKeepOfflineChange(OfflineWallUsage wall, ChangeEventArgs e) =>
        WriteAsync("bwAssetReport.setKeepOffline", wall.WallId, e.Value is true);

    private async Task ClearAsync()
    {
        confirmingClear = false;
        await WriteAsync("bwAssetReport.clear");
    }

    private async Task WriteAsync(string identifier, params object?[] args)
    {
        busy = true;
        error = null;
        try
        {
            await JS.InvokeVoidAsync(identifier, args);
        }
        catch (JSException)
        {
            error = "This device didn't accept the change. Try again.";
        }

        await ReloadAsync();
        busy = false;
    }
}
