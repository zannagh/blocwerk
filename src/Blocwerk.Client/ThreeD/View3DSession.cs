// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components;

namespace Blocwerk.Client.ThreeD;

/// <summary>
/// One 3D stage's life (<see cref="Wall3DView"/>): what the device holds for the wall it shows, the mount once the stage
/// element is on the page, and the unmount when it goes or shows another wall. Nothing is mounted unless the status is
/// ready, so a wall the device holds no 3D for loads none of the 3D code; a mount that comes back after the stage moved
/// on is dropped by client-3d-stage.js ("stale").
/// </summary>
public sealed class View3DSession
{
    private readonly IClient3D client;
    private object? key;
    private bool due;
    private ElementReference? mounted;

    /// <summary>Initializes a new instance of the <see cref="View3DSession"/> class.</summary>
    /// <param name="client">The browser side.</param>
    public View3DSession(IClient3D client)
    {
        this.client = client;
    }

    /// <summary>Gets what the device holds; null until read.</summary>
    public Client3DStatus? Status { get; private set; }

    /// <summary>Gets why a ready view did not start, or null.</summary>
    public string? Failure { get; private set; }

    /// <summary>Gets a value indicating whether the stage element should be on the page.</summary>
    public bool ShowsStage => Status?.Kind == Client3DState.Ready && Failure is null;

    /// <summary>
    /// Reads the status for <paramref name="newKey"/> (the wall, boulder and label shown) when it changed; a viewer of the
    /// previous one is unmounted first.
    /// </summary>
    /// <param name="newKey">What the stage shows.</param>
    /// <param name="partition">The store's label, or null when it reads nothing.</param>
    /// <param name="wallId">The wall.</param>
    /// <returns>A task.</returns>
    public async Task LoadAsync(object newKey, string? partition, Guid wallId)
    {
        if (Equals(key, newKey))
        {
            return;
        }

        key = newKey;
        await UnmountAsync();
        Failure = null;
        Status = partition is null ? Client3DStatus.None : await client.StatusAsync(partition, wallId);
        due = Status.Kind == Client3DState.Ready;
    }

    /// <summary>Mounts the viewer into <paramref name="stage"/> once per status read.</summary>
    /// <param name="stage">The stage element.</param>
    /// <param name="request">What to mount (its <c>Sha</c> is the status's).</param>
    /// <returns>True when the page should render again (a failure or a model gone meanwhile).</returns>
    public async Task<bool> MountAsync(ElementReference stage, Func<Client3DStatus, Client3DRequest> request)
    {
        if (!due || Status is null)
        {
            return false;
        }

        due = false;
        mounted = stage;
        var result = await client.MountAsync(stage, request(Status));
        switch (result)
        {
            case "ok":
            case "stale":
                return false;
            case "missing":
                mounted = null;
                Status = Status with { State = "missing" };
                return true;
            default:
                mounted = null;
                Failure = FailureText(result);
                return true;
        }
    }

    /// <summary>Frees the mounted viewer, if any.</summary>
    /// <returns>A task.</returns>
    public async Task UnmountAsync()
    {
        if (mounted is { } stage)
        {
            mounted = null;
            await client.UnmountAsync(stage);
        }
    }

    private static string FailureText(string result) => result switch
    {
        "model" => "This wall's 3D model could not be drawn.",
        "webgl" => "This device could not start the 3D view (WebGL is unavailable).",
        _ => "The 3D viewer could not be loaded on this device.",
    };
}
