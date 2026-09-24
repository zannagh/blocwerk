// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Runners;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>One runner: state, capabilities, current job; the owner's share / walls / revoke controls.</summary>
public partial class GpuRunnerRow
{
    private IReadOnlyList<GpuRunnerWallChoice>? walls;
    private bool confirmRevoke;

    [Parameter]
    [EditorRequired]
    public GpuRunnerInfo Runner { get; set; } = default!;

    [Parameter]
    public Guid? WallId { get; set; }

    [Parameter]
    public bool Busy { get; set; }

    /// <summary>Owner controls (share, walls, revoke); off in the read-only site-admin list.</summary>
    [Parameter]
    public bool ShowControls { get; set; } = true;

    /// <summary>A site admin may revoke anyone's runner.</summary>
    [Parameter]
    public bool AllowRevokeAsAdmin { get; set; }

    [Parameter]
    public EventCallback OnChanged { get; set; }

    [Parameter]
    public EventCallback<string> OnError { get; set; }

    [Inject]
    private IGpuRunnerService Runners { get; set; } = default!;

    private string Capabilities()
    {
        var c = Runner.Capabilities;
        if (c.GpuName is null && c.RunnerVersion is null)
        {
            return "has not connected yet";
        }

        var vram = c.VramMb is { } mb ? $" ({mb / 1024.0:0.#} GB)" : string.Empty;
        var max = c.MaxQuality is null ? string.Empty : $" · up to {c.MaxQuality} quality";
        return $"{c.GpuName ?? "unknown GPU"}{vram}{max} · runner {c.RunnerVersion ?? "?"}";
    }

    private Task SetSharedAsync(bool shared) => RunAsync(() => Runners.SetSharedAsync(Runner.Id, shared));

    private Task RevokeAsync() => RunAsync(() => Runners.RevokeAsync(Runner.Id));

    private async Task ToggleWallsAsync()
    {
        if (walls is not null)
        {
            walls = null;
            return;
        }

        await RunAsync(async () => walls = await Runners.GetWallChoicesAsync(Runner.Id), notify: false);
    }

    private Task SetWallAsync(Guid wallId, bool serves) => RunAsync(async () =>
    {
        await Runners.SetServesWallAsync(Runner.Id, wallId, serves);
        walls = await Runners.GetWallChoicesAsync(Runner.Id);
    });

    private async Task RunAsync(Func<Task> action, bool notify = true)
    {
        try
        {
            await action();
            confirmRevoke = false;
            if (notify)
            {
                await OnChanged.InvokeAsync();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or UserFacingException or KioskRestrictedException)
        {
            await OnError.InvokeAsync(ex.Message);
        }
    }
}
