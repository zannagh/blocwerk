// <copyright file="RefreshSortScreen.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Assets;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Runners;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>The sort screen: the proposed photo per panel with a confidence badge, changeable (behind "Change") before "Start".</summary>
public partial class RefreshSortScreen
{
    private readonly Dictionary<(int Col, int Row), Guid?> changes = [];
    private readonly HashSet<(int Col, int Row)> editing = [];
    private bool ultraAvailable;

    // The current panels' photo hashes, read from the database each time the view is (re)loaded.
    private IReadOnlyDictionary<Guid, string> photoShas = new Dictionary<Guid, string>();
    private WallRefreshView? photoShasFor;

    [Parameter]
    public WallRefreshView View { get; set; } = default!;

    [Parameter]
    public EventCallback<IReadOnlyList<PanelChoice>> OnStart { get; set; }

    [Parameter]
    public EventCallback<IReadOnlyList<PanelChoice>> OnStartPanelsOnly { get; set; }

    /// <summary>Full revision (the wall was fully reset), with a new 3D capture when one is available.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<PanelChoice>> OnStartOver { get; set; }

    /// <summary>Full revision, panels only.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<PanelChoice>> OnStartOverPanelsOnly { get; set; }

    /// <summary>The photo-real view's quality the 3D capture is started with (the page keeps it for the start).</summary>
    [Parameter]
    public SplatQuality Quality { get; set; } = SplatQuality.High;

    [Parameter]
    public EventCallback<SplatQuality> QualityChanged { get; set; }

    [Inject]
    private IWallCaptureService Captures { get; set; } = default!;

    [Inject]
    private SplatQualityOffer QualityOffer { get; set; } = default!;

    [Inject]
    private IAssetHashReader AssetHashes { get; set; } = default!;

    private bool QualityChoice => View.CaptureAvailable && Captures.IsSplatConfigured;

    protected override async Task OnParametersSetAsync()
    {
        // Ultra only when a 3D runner (or the splat worker) can really train it, as on the capture panel.
        ultraAvailable = QualityChoice && await QualityOffer.UltraAvailableAsync(View.WallId, CancellationToken.None);
        if (!ReferenceEquals(View, photoShasFor))
        {
            photoShas = await AssetHashes.PanelPhotosAsync(View.WallId);
            photoShasFor = View;
        }
    }

    private string CurrentPhotoUrl(Guid panelId) => AssetUrls.Panel(View.WallId, panelId, photoShas.GetValueOrDefault(panelId));

    private Task SetQualityAsync(ChangeEventArgs e) =>
        Enum.TryParse<SplatQuality>(e.Value?.ToString(), out var chosen) ? QualityChanged.InvokeAsync(chosen) : Task.CompletedTask;

    private List<string> PanelsKeepingPhoto => View.Picks.Where(p => Chosen(p) is null).Select(PanelName).ToList();

    private string VideoText => View.Videos.Count == 0 ? string.Empty : $" and {View.Videos.Count} videos";

    private static string PanelName(PanelPick pick) => PanelPositionName.Describe(pick.Col, pick.Row);

    private static string Percent(double share) => $"{Math.Round(share * 100):0} %";

    private List<CapturePhotoResult> Others(PanelPick pick) =>
        View.Photos.Where(p => pick.Candidates.All(c => c.PhotoId != p.PhotoId)).ToList();

    private static string NewCaption(PanelPick pick, Guid? chosen) =>
        pick.Candidates.FirstOrDefault(c => c.PhotoId == chosen) is { } candidate
            ? $"New · shows {Percent(candidate.Coverage)} of the panel"
            : "New";

    private Guid? Chosen(PanelPick pick) =>
        changes.TryGetValue((pick.Col, pick.Row), out var changed) ? changed : pick.PhotoId;

    private bool Changed(PanelPick pick, Guid? chosen) => chosen != pick.PhotoId;

    private string BadgeText(PanelPick pick, Guid? chosen)
    {
        if (Changed(pick, chosen))
        {
            return chosen is null ? "Keeps its photo" : "Your choice";
        }

        return pick.Confidence switch
        {
            PanelPickConfidence.High => "Good match",
            PanelPickConfidence.Medium => "Check this match",
            _ => "No match found",
        };
    }

    private string BadgeClass(PanelPick pick, Guid? chosen) =>
        Changed(pick, chosen) ? "is-user" : pick.Confidence switch
        {
            PanelPickConfidence.High => "is-high",
            PanelPickConfidence.Medium => "is-medium",
            _ => "is-none",
        };

    private string PhotoName(Guid photoId)
    {
        var photo = View.Photos.FirstOrDefault(p => p.PhotoId == photoId);
        return photo?.FileName ?? $"Photo {photo?.Index}";
    }

    private void ToggleEdit(PanelPick pick)
    {
        if (!editing.Remove((pick.Col, pick.Row)))
        {
            editing.Add((pick.Col, pick.Row));
        }
    }

    private void Choose(PanelPick pick, string? value) =>
        changes[(pick.Col, pick.Row)] = Guid.TryParse(value, out var id) ? id : null;

    private IReadOnlyList<PanelChoice> Choices() => View.Picks.Select(p => new PanelChoice(p.Col, p.Row, Chosen(p))).ToList();

    private Task StartAsync() => OnStart.InvokeAsync(Choices());

    private Task StartPanelsOnlyAsync() => OnStartPanelsOnly.InvokeAsync(Choices());

    private Task StartOverAsync() => OnStartOver.InvokeAsync(Choices());

    private Task StartOverPanelsOnlyAsync() => OnStartOverPanelsOnly.InvokeAsync(Choices());

    private static string RevisionQuestion(RefreshRevisionPreview revision) =>
        $"Start {revision.WallName} over? When you apply the update, {Count(revision.Holds, "hold is", "holds are")} replaced by a fresh "
        + $"detection and {Count(revision.Boulders, "boulder is", "boulders are")} archived. Nothing is carried over, and it cannot be undone from the app.";

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
