using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "remove location data from this wall's stored photos" wall-admin action: a dry-run
/// count first, then the cleanup through <see cref="IWallPhotoPrivacyService"/>, then its report.
/// </summary>
public partial class WallPhotoPrivacy
{
    private Guid loadedWallId;
    private bool busy;
    private string? message;
    private string? failure;
    private PhotoPrivacyReport? scan;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the action is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private IWallPhotoPrivacyService Privacy { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallPhotoPrivacy> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    /// <summary>The dry-run sentence: how many stored photos carry a location, and how many carry metadata to remove.</summary>
    internal static string ScanText(PhotoPrivacyReport r)
    {
        var repair = r.NeedsRepair > 0
            ? $" {r.NeedsRepair} gallery {(r.NeedsRepair == 1 ? "snapshot needs" : "snapshots need")} a repair after an interrupted clean-up."
            : string.Empty;
        if (r.WithMetadata == 0)
        {
            return (r.Photos == 0
                ? "This wall stores no photos yet."
                : $"All {r.Photos} stored photos are already free of location and other metadata.") + repair;
        }

        return $"{r.WithLocation} of {r.Photos} stored photos carry a location; "
            + $"{r.WithMetadata} carry metadata that would be removed." + repair;
    }

    /// <summary>
    /// Whether the scan leaves anything for the apply run: metadata to remove, or a gallery row to repair —
    /// the latter on an otherwise clean wall too, or nothing could ever start the run that repairs it.
    /// </summary>
    internal static bool CanApply(PhotoPrivacyReport r) => r.WithMetadata > 0 || r.NeedsRepair > 0;

    /// <summary>The apply button: removing metadata when there is any, otherwise only the repair.</summary>
    internal static string ApplyLabel(PhotoPrivacyReport r) => r.WithMetadata > 0 ? "Remove location data" : "Repair";

    // Route data is reset here, not in OnInitializedAsync: WallDetail is retained across enhanced
    // navigation between walls, and so is this component.
    protected override void OnParametersSet()
    {
        if (WallId == loadedWallId)
        {
            return;
        }

        loadedWallId = WallId;
        scan = null;
        message = null;
        failure = null;
    }

    private Task ScanAsync() => RunAsync(async () =>
    {
        scan = await Privacy.ScanAsync(WallId);
    });

    private Task ApplyAsync() => RunAsync(async () =>
    {
        var result = await Privacy.RemoveMetadataAsync(WallId);
        scan = null;
        message = $"Removed location and other metadata from {result.Cleaned} {(result.Cleaned == 1 ? "photo" : "photos")}.";
        if (result.Skipped > 0)
        {
            message += $" {result.Skipped} changed while this ran — check again to catch them.";
        }

        if (result.NeedsRepair > 0)
        {
            message += $" Repaired {result.NeedsRepair} gallery {(result.NeedsRepair == 1 ? "snapshot" : "snapshots")}.";
        }
    });

    /// <summary>Runs one action with the busy flag set; failures become the status line.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        // A double tap lands before the disabled button reaches the phone: refuse the second run.
        if (busy)
        {
            return;
        }

        busy = true;
        message = null;
        failure = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Photo metadata action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            busy = false;
        }
    }
}
