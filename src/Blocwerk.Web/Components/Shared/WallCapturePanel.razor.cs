using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Code-behind of the capture upload: one draft per admin and wall, photos analysed on upload (the
/// declarations table is built from the marker segments they actually show), then handed to the
/// background pipeline.
/// </summary>
public partial class WallCapturePanel
{
    private Guid loadedWallId;
    private bool configured;
    private bool busy;
    private bool confirmDiscard;
    private string? uploadStatus;
    private List<string> errors = [];
    private WallCaptureDraft? draft;
    private List<CaptureDeclarationRow> declarations = [];
    private string? levelPairs;
    private string? notes;
    private SplatQuality splatQuality = SplatQuality.High;
    private bool ultraAvailable;
    private List<string> planNotes = [];
    private WallCaptureStatusList? statusList;

    [Parameter]
    public Guid WallId { get; set; }

    /// <summary>Raised when a capture finished and a new model became active.</summary>
    [Parameter]
    public EventCallback OnModelActivated { get; set; }

    /// <summary>Raised with the panel id when a capture photo was staged as a new panel.</summary>
    [Parameter]
    public EventCallback<Guid> OnPanelStaged { get; set; }

    /// <summary>Raised with the chosen capture photos to start a full wall update with.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<CaptureUpdatePhoto>> OnStartWallUpdate { get; set; }

    /// <summary>Raised when the admin follows the link to where panel photos are managed.</summary>
    [Parameter]
    public EventCallback OnShowPanels { get; set; }

    [Inject]
    private IWallCaptureService Captures { get; set; } = default!;

    [Inject]
    private ILogger<WallCapturePanel> Logger { get; set; } = default!;

    [Inject]
    private WallCapturePipelineOptions PipelineOptions { get; set; } = default!;

    [Inject]
    private Blocwerk.Core.Runners.SplatQualityOffer QualityOffer { get; set; } = default!;

    private long MaxPhotoMb => PipelineOptions.MaxPhotoBytes / (1024 * 1024);

    // Loaded per wall here, not in OnInitializedAsync: the wall page is retained across walls.
    protected override async Task OnParametersSetAsync()
    {
        if (WallId == loadedWallId)
        {
            return;
        }

        loadedWallId = WallId;
        configured = Captures.IsComputeConfigured;
        errors = [];
        planNotes = [];
        draft = null;
        declarations = [];
        confirmDiscard = false;
        ultraAvailable = false;
        if (configured)
        {
            await RunAsync(async () =>
            {
                // Ultra only when a 3D runner (or the splat worker) can really train it.
                ultraAvailable = Captures.IsSplatConfigured && await QualityOffer.UltraAvailableAsync(WallId, CancellationToken.None);
                draft = await Captures.GetDraftAsync(WallId);
                await RefreshDeclarationsAsync(keepEdits: false);
            });
        }
    }

    private async Task UploadAsync(InputFileChangeEventArgs e)
    {
        // GetMultipleFiles THROWS when more files were picked than the limit it is given (outside any
        // catch, so it would end the circuit): take them all and trim to the room left, saying so.
        var picked = e.GetMultipleFiles(Math.Max(e.FileCount, 1));
        await RunAsync(async () =>
        {
            draft ??= await Captures.CreateDraftAsync(WallId);
            var room = Math.Max(0, WallCapturePipelineOptions.MaxPhotos - draft.Photos.Count);
            var selected = picked.Take(room).ToList();
            for (var i = 0; i < selected.Count; i++)
            {
                uploadStatus = $"Analysing photo {i + 1} of {selected.Count}…";
                StateHasChanged();
                await UploadOneAsync(selected[i]);
            }

            draft = await Captures.GetDraftAsync(WallId);
            await RefreshDeclarationsAsync(keepEdits: true);
            if (picked.Count > room)
            {
                errors.Add(TooManyPhotosNote(picked.Count, room));
            }
        });
        uploadStatus = null;
    }

    private static string TooManyPhotosNote(int picked, int room) => room == 0
        ? $"This capture already has {WallCapturePipelineOptions.MaxPhotos} photos, the most it takes. Remove some before adding more."
        : $"A capture takes at most {WallCapturePipelineOptions.MaxPhotos} photos, so only the first {room} of the {picked} you picked were added.";

    private async Task UploadOneAsync(IBrowserFile file)
    {
        if (file.Size > PipelineOptions.MaxPhotoBytes)
        {
            errors.Add($"{file.Name} is larger than {MaxPhotoMb} MB.");
            return;
        }

        try
        {
            await using var stream = file.OpenReadStream(PipelineOptions.MaxPhotoBytes);
            using var buffer = new MemoryStream((int)file.Size);
            await stream.CopyToAsync(buffer);
            await Captures.AddPhotoAsync(draft!.CaptureId, file.Name, buffer.ToArray(), CancellationToken.None);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add(ex.Message);
        }
        catch (InvalidDataException ex)
        {
            // A corrupt image fails in the metadata stripper; one bad file must not end the upload.
            Logger.LogWarning(ex, "Capture photo {FileName} could not be read", file.Name);
            errors.Add($"{file.Name}: this photo couldn't be read.");
        }
    }

    private async Task UploadPlanAsync(InputFileChangeEventArgs e)
    {
        var file = e.File;
        await RunAsync(async () =>
        {
            if (file.Size > MarkerPlanJson.MaxBytes)
            {
                errors.Add($"{file.Name} is larger than {MarkerPlanJson.MaxBytes / 1024} KB — is it really a marker plan?");
                return;
            }

            await using var stream = file.OpenReadStream(MarkerPlanJson.MaxBytes);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync();
            draft ??= await Captures.CreateDraftAsync(WallId);
            await ApplyPlanAsync(json);
        });
    }

    private Task RemovePlanAsync() => RunAsync(() => ApplyPlanAsync(null));

    /// <summary>Uses (or drops) the plan; its segments, names and angles replace the table's rows.</summary>
    private async Task ApplyPlanAsync(string? json)
    {
        var result = await Captures.AttachPlanAsync(draft!.CaptureId, json);
        planNotes = [.. result.Notes];
        if (!result.Accepted)
        {
            errors.AddRange(result.Errors.Take(8).Prepend("This marker plan can't be used:"));
            return;
        }

        draft = await Captures.GetDraftAsync(WallId);
        await RefreshDeclarationsAsync(keepEdits: false);
    }

    private Task RemovePhotoAsync(Guid photoId) => RunAsync(async () =>
    {
        await Captures.RemovePhotoAsync(draft!.CaptureId, photoId);
        draft = await Captures.GetDraftAsync(WallId);
        await RefreshDeclarationsAsync(keepEdits: true);
    });

    private async Task ReloadDraftAsync() => draft = await Captures.GetDraftAsync(WallId);

    private Task DiscardAsync() => RunAsync(async () =>
    {
        confirmDiscard = false;
        await Captures.DiscardDraftAsync(draft!.CaptureId);
        draft = null;
        declarations = [];
        levelPairs = null;
        notes = null;
        planNotes = [];
    });

    private Task StartAsync() => RunAsync(async () =>
    {
        var (pairs, pairError) = CaptureDeclarationRules.ParseLevelPairs(
            levelPairs, draft!.Plan?.MaxMarkerId ?? CaptureDeclarationRules.MaxMarkerId);
        if (pairError is not null)
        {
            errors.Add(pairError);
            return;
        }

        var problems = await Captures.StartAsync(
            draft!.CaptureId, new CaptureDeclarations(declarations.Select(r => r.ToDeclaration()).ToList(), pairs), notes, splatQuality);
        if (problems.Count > 0)
        {
            errors.AddRange(problems);
            return;
        }

        draft = null;
        declarations = [];
        notes = null;
        if (statusList is not null)
        {
            await statusList.RefreshAsync();
        }
    });

    /// <summary>Rebuilds the table from the segments now seen, keeping what the admin already typed.</summary>
    private async Task RefreshDeclarationsAsync(bool keepEdits)
    {
        if (draft is null)
        {
            declarations = [];
            return;
        }

        var suggested = await Captures.SuggestDeclarationsAsync(draft.CaptureId);
        var edited = keepEdits ? declarations.ToDictionary(r => r.Index) : [];
        declarations = suggested.Segments
            .Select(s => edited.GetValueOrDefault(s.Index) ?? CaptureDeclarationRow.From(s))
            .ToList();
        if (!keepEdits || string.IsNullOrWhiteSpace(levelPairs))
        {
            levelPairs = CaptureDeclarationRules.FormatLevelPairs(suggested.LevelPairs);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        // Every capture action goes through here, so a double tap never runs one twice.
        if (busy)
        {
            return;
        }

        busy = true;
        errors = [];
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or KioskRestrictedException or IOException)
        {
            Logger.LogWarning(ex, "Capture action failed for wall {WallId}", WallId);
            errors.Add(ex.Message);
        }
        finally
        {
            busy = false;
        }
    }
}
