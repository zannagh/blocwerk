// <copyright file="EditActivityDeployBusyGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Web.State;

/// <summary>
/// <see cref="IDeployBusyGate"/> on top of <see cref="EditActivityRegistry"/>: a hold is an ordinary
/// lease, so it shows up in the <c>busy</c> health check exactly like an open editor or a
/// maintenance job. The kinds it uses are background work (see
/// <see cref="EditActivityPolicy.IsBackgroundWork"/>): nothing heartbeats them, so only the absolute
/// cap bounds a hold whose owner never disposes it.
/// </summary>
public sealed class EditActivityDeployBusyGate : IDeployBusyGate
{
    private readonly EditActivityRegistry registry;

    public EditActivityDeployBusyGate(EditActivityRegistry registry)
    {
        this.registry = registry;
    }

    public IDisposable Hold(DeployBusyWork work, Guid? wallId = null, Guid? userId = null)
    {
        var kind = work switch
        {
            DeployBusyWork.CaptureVideoUpload => EditKind.CaptureVideoUpload,
            DeployBusyWork.CaptureVideoFrames => EditKind.CaptureVideoFrames,
            _ => EditKind.Maintenance,
        };
        return registry.Acquire(kind, wallId, userId);
    }
}
