using Blocwerk.Core.Capture;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the "use as panel photo" picker. It offers the panel grid's own "+" cells for a new
/// panel and the uploader's slots for a full wall update, and hands both to
/// <see cref="ICapturePanelPhotoService"/>, which reuses the existing staging paths unchanged.
/// </summary>
public partial class WallCapturePanelPicker
{
    private readonly List<BigWallUpdateUploaderSlot> slots = BigWallUpdateUploaderSlot.CreateAll();
    private readonly Dictionary<Guid, string> choices = [];
    private (Guid WallId, Guid CaptureId) loadedFor;
    private IReadOnlyList<PanelPosition> frontier = [];
    private HashSet<(int Col, int Row)> liveCells = [];
    private bool busy;
    private string? error;
    private string? loadFailure;

    [Parameter]
    public Guid WallId { get; set; }

    [Parameter]
    public Guid CaptureId { get; set; }

    [Parameter]
    [EditorRequired]
    public IReadOnlyList<CapturePhotoResult> Photos { get; set; } = [];

    [Parameter]
    public bool Disabled { get; set; }

    /// <summary>Open the section straight away (the admin already asked for these photos).</summary>
    [Parameter]
    public bool StartOpen { get; set; }

    /// <summary>Raised with the staged panel's id once a photo was staged as a new panel.</summary>
    [Parameter]
    public EventCallback<Guid> OnPanelStaged { get; set; }

    /// <summary>Raised with the chosen photos when the admin starts a full wall update with them.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<CaptureUpdatePhoto>> OnStartWallUpdate { get; set; }

    [Inject]
    private IWallPanelService Panels { get; set; } = default!;

    [Inject]
    private ICapturePanelPhotoService CapturePanels { get; set; } = default!;

    [Inject]
    private ILogger<WallCapturePanelPicker> Logger { get; set; } = default!;

    private List<(Guid PhotoId, CapturePanelTarget Target)> Chosen => choices
        .Select(c => (c.Key, CapturePanelTarget.Parse(c.Value)))
        .Where(c => c.Item2 is not null)
        .Select(c => (c.Key, c.Item2!))
        .ToList();

    private bool CanStageNewPanel => Chosen is [{ Target.IsNewPanel: true }];

    private bool CanStartWallUpdate
    {
        get
        {
            var chosen = Chosen;
            return chosen.Count > 0
                && chosen.All(c => !c.Target.IsNewPanel)
                && chosen.Select(c => (c.Target.Col, c.Target.Row)).Distinct().Count() == chosen.Count;
        }
    }

    private string? Guidance
    {
        get
        {
            var chosen = Chosen;
            if (chosen.Count == 0)
            {
                return null;
            }

            if (chosen.Any(c => c.Target.IsNewPanel) && chosen.Count > 1)
            {
                return "A new panel is added one photo at a time. Stage it on its own, then come back for the rest.";
            }

            if (chosen.Select(c => (c.Target.Col, c.Target.Row)).Distinct().Count() != chosen.Count)
            {
                return "Two photos point at the same panel. Pick a different cell for one of them.";
            }

            return chosen.Any(c => !c.Target.IsNewPanel) && !chosen.Any(c => c.Target is { Col: 0, Row: 0 })
                ? "A wall update always includes the centre photo. You can add it in the next step."
                : null;
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (loadedFor == (WallId, CaptureId))
        {
            return;
        }

        loadedFor = (WallId, CaptureId);
        choices.Clear();
        error = null;
        await LoadGridAsync();
    }

    /// <summary>The "+" cells and live cells, re-read after staging (the staged cell is no longer free).</summary>
    private async Task LoadGridAsync()
    {
        try
        {
            frontier = await Panels.GetFrontierPositionsAsync(WallId);
            liveCells = (await Panels.GetPanelsAsync(WallId)).Where(p => p.IsLive).Select(p => (p.Col, p.Row)).ToHashSet();
            loadFailure = null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Could not load the panel grid of wall {WallId}", WallId);
            loadFailure = "The wall's panels could not be loaded.";
        }
    }

    private static string PhotoName(CapturePhotoResult photo) => photo.FileName ?? $"Photo {photo.Index}";

    private string SlotLabel(BigWallUpdateUploaderSlot slot) =>
        $"{slot.Label} ({slot.Col},{slot.Row}) · "
        + (liveCells.Contains((slot.Col, slot.Row)) ? "replaces its photo" : "new panel");

    private void Choose(Guid photoId, object? value)
    {
        error = null;
        if (value is string key && CapturePanelTarget.Parse(key) is not null)
        {
            choices[photoId] = key;
        }
        else
        {
            choices.Remove(photoId);
        }
    }

    private async Task StageNewPanelAsync()
    {
        if (Chosen is not [var (photoId, target)] || !target.IsNewPanel)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await CapturePanels.StageAsNewPanelAsync(CaptureId, photoId, target.Col, target.Row);
            choices.Clear();
            await LoadGridAsync();
            await OnPanelStaged.InvokeAsync(result.PanelId);
        });
    }

    private async Task StartWallUpdateAsync()
    {
        if (!CanStartWallUpdate)
        {
            return;
        }

        var assignments = Chosen
            .Select(c => new CapturePanelAssignment(c.PhotoId, c.Target.Col, c.Target.Row))
            .ToList();
        await RunAsync(async () =>
        {
            var photos = await CapturePanels.PrepareWallUpdateAsync(CaptureId, assignments, CancellationToken.None);
            await OnStartWallUpdate.InvokeAsync(photos);
        });
    }

    // Staging runs hold detection and the overlap matcher, which can fail in more ways than the
    // guards; like the panel grid's own "+" upload, any failure is shown instead of killing the circuit.
    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        error = null;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Using a capture photo as a panel photo failed on wall {WallId}", WallId);
            error = ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException
                ? ex.Message
                : "The photo could not be used as a panel photo. Please try again.";
        }
        finally
        {
            busy = false;
        }
    }
}
