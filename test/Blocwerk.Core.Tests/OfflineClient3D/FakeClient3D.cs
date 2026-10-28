// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.ThreeD;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Core.Tests.OfflineClient3D;

/// <summary>The 3D view's browser side for the tests: a set status, a set mount answer, and a log of the calls.</summary>
internal sealed class FakeClient3D : IClient3D
{
    public Client3DStatus Status { get; set; } = Client3DStatus.None;

    public string MountAnswer { get; set; } = "ok";

    public List<(string Partition, Guid WallId)> StatusReads { get; } = [];

    public List<Client3DRequest> Mounts { get; } = [];

    public int Unmounts { get; private set; }

    public Task<Client3DStatus> StatusAsync(string partition, Guid wallId)
    {
        StatusReads.Add((partition, wallId));
        return Task.FromResult(Status);
    }

    public Task<string> MountAsync(ElementReference element, Client3DRequest request)
    {
        Mounts.Add(request);
        return Task.FromResult(MountAnswer);
    }

    public Task UnmountAsync(ElementReference element)
    {
        Unmounts++;
        return Task.CompletedTask;
    }
}
