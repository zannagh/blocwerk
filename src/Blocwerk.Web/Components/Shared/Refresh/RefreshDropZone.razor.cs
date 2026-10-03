// <copyright file="RefreshDropZone.razor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

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

    /// <summary>Opens the run if there is none yet; returns its id (null when it could not be opened).</summary>
    [Parameter]
    public Func<Task<Guid?>> EnsureRefreshAsync { get; set; } = default!;

    [Parameter]
    public EventCallback OnFilesChanged { get; set; }

    [Parameter]
    public EventCallback OnContinue { get; set; }

    [Inject]
    private IJSRuntime JS { get; set; } = default!;

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
            if (await EnsureRefreshAsync() is not { } refreshId)
            {
                return;
            }

            selfRef ??= DotNetObjectReference.Create(this);
            await JS.InvokeAsync<int>(
                "bwRefreshUpload.upload", TimeSpan.FromHours(3), "refresh-files", WallRefreshUploadEndpoint.Url(refreshId), selfRef);
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
