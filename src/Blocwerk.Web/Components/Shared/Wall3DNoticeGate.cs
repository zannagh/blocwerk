// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The mobile-data notice's state for one stage (<see cref="Wall3DStage"/>): ask once, then remember the answer. A Continue
/// lets every later mount through; a Cancel is latched, so a live rebuild of the view while the notice is open, or after
/// it, never prompts again. Only <see cref="Load"/> (the "Load 3D" button) lifts the latch.
/// </summary>
public sealed class Wall3DNoticeGate
{
    private Wall3DNoticeState? open;

    /// <summary>The notice while it waits for the viewer, or null.</summary>
    public Wall3DNoticeState? Open => open;

    /// <summary>True after a Cancel, until <see cref="Load"/>.</summary>
    public bool Declined { get; private set; }

    /// <summary>True once the viewer continued (or nothing had to be asked).</summary>
    public bool Passed { get; private set; }

    /// <summary>
    /// Whether the mount may go on. <paramref name="ask"/> is the client's gate (null on any failure: no notice);
    /// <paramref name="shown"/> re-renders the stage once the notice is open.
    /// </summary>
    public async Task<bool> PassAsync(Func<Task<Wall3DDataGate?>> ask, Func<Task> shown)
    {
        if (Passed)
        {
            return true;
        }

        if (Declined)
        {
            return false;
        }

        if (open is { } waiting)
        {
            // A second mount while the notice is up waits for the same answer.
            return await waiting.Answer.Task;
        }

        var gate = await ask();
        if (gate is not { Ask: true })
        {
            Passed = true;
            return true;
        }

        var notice = new Wall3DNoticeState(gate.Bytes, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        open = notice;
        await shown();
        return await notice.Answer.Task;
    }

    /// <summary>The viewer's answer to the open notice.</summary>
    public void Answer(bool go)
    {
        if (open is not { } waiting)
        {
            return;
        }

        open = null;
        Passed = go;
        Declined = !go;
        waiting.Answer.TrySetResult(go);
    }

    /// <summary>"Load 3D" after a Cancel: the next mount goes through.</summary>
    public void Load()
    {
        Declined = false;
        Passed = true;
    }

    /// <summary>The stage went away: a waiting mount ends without mounting.</summary>
    public void Abandon()
    {
        var waiting = open;
        open = null;
        waiting?.Answer.TrySetResult(false);
    }
}
