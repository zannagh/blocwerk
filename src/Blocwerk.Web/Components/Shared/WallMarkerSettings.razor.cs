using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind for the wall's printed-marker settings: loads the declaration, the active geometry,
/// its history and the wall's segments, and routes every action through <see cref="IWallGlyphService"/>.
/// </summary>
public partial class WallMarkerSettings
{
    private const long MaxImportBytes = WallGlyphService.MaxJsonLength;

    private Guid loadedWallId;
    private bool loading = true;
    private bool busy;
    private string? message;
    private string? failure;
    private WallGlyphSettings settings = new(false, null);
    private bool draftEnabled;
    private double draftSizeMm = WallGlyphSettings.DefaultMarkerSizeMm;
    private ActiveWallGeometry? active;
    private IReadOnlyList<WallGeometryHistoryEntry> history = [];
    private List<WallSegment> segments = [];
    private List<string> importErrors = [];
    private string? importNotes;
    private bool markerless;
    private bool markerlessAvailable;

    /// <summary>The wall being administered.</summary>
    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Passed through from the capture section: a capture photo was staged as a new panel.</summary>
    [Parameter]
    public EventCallback<Guid> OnPanelStaged { get; set; }

    /// <summary>Passed through from the capture section: start a full wall update with these photos.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<CaptureUpdatePhoto>> OnStartWallUpdate { get; set; }

    /// <summary>Passed through from the capture section: show where panel photos are managed.</summary>
    [Parameter]
    public EventCallback OnShowPanels { get; set; }

    [Inject]
    private IWallGlyphService Glyphs { get; set; } = default!;

    [Inject]
    private IWallSegmentService SegmentService { get; set; } = default!;

    [Inject]
    private IKioskContext KioskContext { get; set; } = default!;

    [Inject]
    private IWallCaptureService Captures { get; set; } = default!;

    [Inject]
    private ILogger<WallMarkerSettings> Logger { get; set; } = default!;

    private bool SettingsDirty =>
        draftEnabled != settings.Enabled
        || (draftEnabled && draftSizeMm != (settings.MarkerSizeMm ?? WallGlyphSettings.DefaultMarkerSizeMm));

    // Route data is loaded here, not in OnInitializedAsync: WallDetail is retained across enhanced
    // navigation between walls, and so is this component.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId || KioskContext.IsKiosk)
        {
            return;
        }

        loadedWallId = WallId;
        loading = true;
        message = null;
        failure = null;
        importErrors = [];
        await ReloadAsync();
        loading = false;
    }

    private async Task ReloadAsync()
    {
        try
        {
            settings = await Glyphs.GetGlyphSettingsAsync(WallId);
            draftEnabled = settings.Enabled;
            draftSizeMm = settings.MarkerSizeMm ?? WallGlyphSettings.DefaultMarkerSizeMm;

            // Walls without markers can be captured too when the server measures from photo features.
            markerlessAvailable = await Captures.IsMarkerlessAvailableAsync();
            markerless = !settings.Enabled && markerlessAvailable;
            active = await Glyphs.GetActiveGeometryAsync(WallId);
            history = await Glyphs.GetGeometryHistoryAsync(WallId);
            segments = (await SegmentService.GetSegmentsAsync(WallId))
                .Where(s => s.Kind == WallSegmentKind.Wall)
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException)
        {
            Logger.LogWarning(ex, "Could not load marker settings for wall {WallId}", WallId);
            failure = "Marker settings could not be loaded.";
        }
    }

    private async Task SaveSettingsAsync()
    {
        await RunAsync(async () =>
        {
            settings = await Glyphs.SetGlyphSettingsAsync(WallId, draftEnabled, draftEnabled ? draftSizeMm : null);
            message = settings.Enabled ? "Markers switched on for this wall." : "Markers switched off for this wall.";
        });
    }

    private async Task ImportAsync(InputFileChangeEventArgs e)
    {
        importErrors = [];
        if (e.File.Size > MaxImportBytes)
        {
            importErrors = ["The file is larger than 2 MB — is it really a wall-geometry.json?"];
            return;
        }

        await RunAsync(async () =>
        {
            using var reader = new StreamReader(e.File.OpenReadStream(MaxImportBytes));
            var json = await reader.ReadToEndAsync();
            var result = await Glyphs.ImportGeometryAsync(WallId, json, importNotes);
            if (!result.Succeeded)
            {
                importErrors = result.Errors.ToList();
                return;
            }

            importNotes = null;
            message = $"Imported {e.File.Name}. It is now the active geometry.";
        });
    }

    private Task ActivateAsync(Guid modelId) => RunAsync(async () =>
    {
        await Glyphs.ActivateGeometryAsync(modelId);
        message = "That geometry is active again.";
    });

    private Task BindSegmentAsync((Guid SegmentId, int? MarkerSegmentIndex) binding) => RunAsync(async () =>
    {
        await Glyphs.SetSegmentMarkerIndexAsync(binding.SegmentId, binding.MarkerSegmentIndex);
        message = "Segment binding saved.";
    });

    /// <summary>A capture finished in the background and activated a model: show it.</summary>
    private async Task ReloadAfterCaptureAsync()
    {
        await ReloadAsync();
        message = "A new 3D model was computed from your photos and is now active.";
    }

    /// <summary>A correction made a new model version active: show it.</summary>
    private async Task ReloadAfterCorrectionAsync(string summary)
    {
        await ReloadAsync();
        message = $"{summary}. The corrected model is now active; the previous one stays in the history.";
    }

    /// <summary>Runs one action with the busy flag set, then reloads; failures become the status line.</summary>
    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        message = null;
        failure = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or KioskRestrictedException or IOException)
        {
            Logger.LogWarning(ex, "Marker settings action failed for wall {WallId}", WallId);
            failure = ex.Message;
        }
        finally
        {
            var keepErrors = importErrors;
            await ReloadAsync();
            importErrors = keepErrors;
            busy = false;
        }
    }
}
