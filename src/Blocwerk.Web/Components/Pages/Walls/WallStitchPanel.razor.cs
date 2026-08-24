using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Stitching;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Job lifecycle behind <c>WallStitchPanel.razor</c>: starting a run, polling it to a terminal
/// state, cancelling it, and handing the finished result to the wall's staging slot.
/// </summary>
public partial class WallStitchPanel
{
    [Parameter] public Guid WallId { get; set; }
    [Parameter] public Guid CurrentUserId { get; set; }

    /// <summary>The wall's inclination in degrees; drives the angled projection.</summary>
    [Parameter] public int WallAngleDegrees { get; set; }

    /// <summary>True while another wall update is already awaiting confirmation.</summary>
    [Parameter] public bool HasStagedPhoto { get; set; }

    /// <summary>Raised once a finished stitch has been written to the wall's staged slot.</summary>
    [Parameter] public EventCallback OnStagingApplied { get; set; }

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private WallStitchJob? _job;
    private StitchJobResult? _result;
    private bool _busy;
    private string? _error;
    private CancellationTokenSource? _pollCts;

    private static bool IsTerminal(WallStitchJobStatus status) =>
        status is WallStitchJobStatus.Succeeded or WallStitchJobStatus.Failed or WallStitchJobStatus.Cancelled;

    protected override async Task OnInitializedAsync()
    {
        if (!StitchClient.IsConfigured || WallId == Guid.Empty)
        {
            return;
        }

        try
        {
            var jobs = await StitchService.GetJobsForWallAsync(WallId);
            _job = jobs.Count > 0 ? jobs[0] : null;
            if (_job is null)
            {
                return;
            }

            if (!IsTerminal(_job.Status))
            {
                StartPolling();
            }
            else if (_job.Status == WallStitchJobStatus.Succeeded)
            {
                _result = await StitchService.GetResultAsync(_job.Id);
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
    }

    private async Task StartJob(WallStitchUploadForm.StitchStartRequest request)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _error = null;
        StateHasChanged();
        try
        {
            var options = new WallStitchStartOptions(WallAngleDegrees, request.Projection);
            _job = await StitchService.StartJobAsync(WallId, CurrentUserId, request.Photos, options);
            _result = null;
            StartPolling();
        }
        catch (UnauthorizedAccessException)
        {
            _error = "Only wall admins can start a stitch.";
        }
        catch (Exception ex)
        {
            _error = $"Could not start the stitch: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CancelJob()
    {
        if (_job is null || _busy)
        {
            return;
        }

        _busy = true;
        StateHasChanged();
        try
        {
            await StitchService.CancelJobAsync(_job.Id, CurrentUserId);
            StopPolling();
            _job = await StitchService.GetJobAsync(_job.Id);
        }
        catch (Exception ex)
        {
            _error = $"Could not cancel: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ApplyToStaging()
    {
        if (_job is null || _busy)
        {
            return;
        }

        _busy = true;
        _error = null;
        StateHasChanged();
        try
        {
            await StitchService.ApplyResultToStagingAsync(_job.Id, CurrentUserId);
            _job = null;
            _result = null;
            await OnStagingApplied.InvokeAsync();
        }
        catch (Exception ex)
        {
            _error = $"Could not stage the result: {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private void Reset()
    {
        StopPolling();
        _job = null;
        _result = null;
        _error = null;
    }

    private void StartPolling()
    {
        StopPolling();
        var cts = new CancellationTokenSource();
        _pollCts = cts;
        _ = PollAsync(cts);
    }

    private void StopPolling()
    {
        var cts = _pollCts;
        _pollCts = null;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// Re-polls the job every few seconds until it reaches a terminal state. The loop owns its own
    /// token, so disposal (or Cancel/Reset) ends it at once, and every render request is wrapped
    /// because the renderer may already be gone by the time a poll returns.
    /// </summary>
    private async Task PollAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                var jobId = _job?.Id;
                if (jobId is null)
                {
                    return;
                }

                var refreshed = await StitchService.RefreshJobAsync(jobId.Value, token);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                if (refreshed is not null)
                {
                    _job = refreshed;
                    if (IsTerminal(refreshed.Status))
                    {
                        if (refreshed.Status == WallStitchJobStatus.Succeeded)
                        {
                            _result = await StitchService.GetResultAsync(refreshed.Id, token);
                        }

                        await SafeRenderAsync();
                        return;
                    }
                }

                await SafeRenderAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _error = $"Lost track of the stitch: {ex.Message}";
            await SafeRenderAsync();
        }
    }

    private async Task SafeRenderAsync()
    {
        try
        {
            await InvokeAsync(StateHasChanged);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        StopPolling();
        return ValueTask.CompletedTask;
    }
}
