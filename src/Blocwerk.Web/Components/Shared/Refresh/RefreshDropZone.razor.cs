// <copyright file="RefreshDropZone.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Refresh;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>
/// The drop zone: opens the run on the first file, streams every file through refresh-upload.js to
/// <see cref="WallRefreshUploadEndpoint"/> and lists the files that were refused, with the reason.
/// </summary>
public partial class RefreshDropZone : IDisposable
{
    private readonly List<RefreshFile> problems = [];
    private DotNetObjectReference<RefreshDropZone>? selfRef;
    private bool uploading;
    private int percent;
    private double sentMb;
    private double totalMb;
    private int uploadedSinceReload;

    [Parameter]
    public WallRefreshView? View { get; set; }

    /// <summary>Opens the run if there is none yet; returns it (null when it could not be opened).</summary>
    [Parameter]
    public Func<Task<WallRefreshView?>> EnsureRefreshAsync { get; set; } = default!;

    [Parameter]
    public EventCallback OnFilesChanged { get; set; }

    [Parameter]
    public EventCallback OnContinue { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private WallCapturePipelineOptions PipelineOptions { get; set; } = default!;

    /// <summary>"12 photos, 2 videos (310 MB)": the count, and the size where it is known (videos; the upload that just ran).</summary>
    private string Counts
    {
        get
        {
            if (View is null)
            {
                return string.Empty;
            }

            var videoMb = View.Videos.Sum(v => v.SizeBytes) / 1048576d;
            var videos = View.Videos.Count == 0 ? string.Empty : $", {PlainCopy.Plural(View.Videos.Count, "video")} ({videoMb:0} MB)";
            var sent = !uploading && totalMb >= 1 ? $" · {totalMb:0} MB sent just now" : string.Empty;
            return $"{PlainCopy.Plural(View.Photos.Count, "photo")}{videos}{sent}";
        }
    }

    /// <summary>Videos would not be used here (no 3D or no photo-real view): the picker does not offer them.</summary>
    private bool NoVideos => View is { VideosAccepted: false };

    public void Dispose() => selfRef?.Dispose();

    [JSInvokable]
    public Task OnUploadProgress(int pct, double sent, double total)
    {
        (percent, sentMb, totalMb) = (pct, sent, total);
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public async Task OnFileUploaded(RefreshFile file)
    {
        if (file.Problem is not null)
        {
            problems.Add(file);
        }

        if (++uploadedSinceReload % 10 == 0)
        {
            await OnFilesChanged.InvokeAsync();
        }

        await InvokeAsync(StateHasChanged);
    }

    private static string Megabytes(long bytes) => $"{bytes / 1048576d:0} MB";

    /// <summary>Why a video is not used here, said before it is sent.</summary>
    private static string VideoRefusal(WallRefreshView run) => run.CaptureAvailable
        ? "Not sent: videos are only used for the photo-real 3D view, which is not set up here."
        : "Not sent: 3D is not set up for this wall, so videos are not used.";

    private async Task UploadAsync()
    {
        if (uploading)
        {
            return;
        }

        uploading = true;
        percent = 0;
        try
        {
            if (await EnsureRefreshAsync() is not { } run)
            {
                return;
            }

            selfRef ??= DotNetObjectReference.Create(this);

            // Photos the capture would refuse, and videos it could not use, are not sent at all: the browser stops them
            // before a byte goes out (a 700 MB video refused after its upload cost minutes for nothing).
            var room = Math.Max(0, PipelineOptions.MaxPhotos - run.Photos.Count);
            var videos = new RefreshVideoRules(
                run.VideosAccepted, Math.Max(0, WallRefreshService.MaxVideos - run.Videos.Count), PipelineOptions.MaxVideoBytes, VideoRefusal(run));
            await JS.InvokeAsync<int>(
                "bwRefreshUpload.upload", TimeSpan.FromHours(3), "refresh-files", WallRefreshUploadEndpoint.Url(run.Id), selfRef,
                room, PipelineOptions.MaxPhotos, videos);
        }
        catch (JSException ex)
        {
            problems.Add(new RefreshFile(null, null, false, $"The upload stopped: {ex.Message}"));
        }
        finally
        {
            uploading = false;
            await OnFilesChanged.InvokeAsync();
        }
    }
}
