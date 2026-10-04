using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the "Sync linked holds" wall-admin action and the "Panel that wins for linked holds" setting,
/// through <see cref="ILinkedHoldSyncService"/> (one change-journal batch per run).
/// </summary>
public partial class WallLinkedHoldSync
{
    private Guid loadedWallId;
    private bool busy;
    private string? message;
    private string? failure;
    private LinkedHoldSyncStatus? status;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Set when the page is viewed through a share link: the action is never offered there.</summary>
    [Parameter]
    public string? ShareToken { get; set; }

    [Inject]
    private ILinkedHoldSyncService Sync { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private ILogger<WallLinkedHoldSync> Logger { get; set; } = default!;

    private bool Hidden => KioskContext.IsKiosk || !string.IsNullOrEmpty(ShareToken);

    private string WinnerValue => status?.Winner is { } w ? $"{w.Col},{w.Row}" : string.Empty;

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced navigation.
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

    private static string Describe(HoldSyncReport report) => report.Any
        ? $"Updated {PlainCopy.Plural(report.HoldsChanged, "hold")}: "
          + string.Join(", ", report.UpdatedByProperty.OrderBy(p => p.Key).Select(p => $"{p.Key} on {p.Value}"))
          + (report.Conflicts.Count > 0 ? $". {PlainCopy.Plural(report.Conflicts.Count, "disagreement")} settled toward the chosen panel." : ".")
        : "All linked holds already agree.";

    private async Task ReloadAsync()
    {
        try
        {
            status = await Sync.GetStatusAsync(WallId);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Logger.LogWarning(ex, "Could not load the linked-hold sync status of wall {WallId}", WallId);
            status = null;
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException or UserFacingException;

    private Task OnWinnerChanged(ChangeEventArgs e) => RunAsync(async () =>
    {
        var parts = (e.Value?.ToString() ?? string.Empty).Split(',');
        (int, int)? cell = parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var r) ? (c, r) : null;
        await Sync.SetWinnerPanelAsync(WallId, cell);
        message = "Saved. It applies the next time linked holds are synced.";
    });

    private Task SyncAsync() => RunAsync(async () =>
    {
        var result = await Sync.SyncAsync(WallId);
        message = Describe(result.Report);
    });

    private Task UndoAsync(Guid batchId) => RunAsync(async () =>
    {
        var result = await Sync.RevertAsync(WallId, batchId);
        message = result.Reverted
            ? "Linked holds restored."
            : "Some holds were edited since the sync, so nothing was undone.";
    });

    private async Task RunAsync(Func<Task> action)
    {
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
        catch (Exception ex) when (IsExpected(ex))
        {
            Logger.LogWarning(ex, "Linked-hold sync action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            busy = false;
            await ReloadAsync();
        }
    }
}
