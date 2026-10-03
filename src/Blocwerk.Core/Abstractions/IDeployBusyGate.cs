// <copyright file="IDeployBusyGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Core's seam to the web host's deploy gate (<c>/health/ready-to-deploy</c>): while a hold is open
/// the app reports busy and the autodeploy hook waits instead of recreating the container. Optional
/// everywhere it is injected — without it (tests, tools) the work simply runs ungated.
/// </summary>
public interface IDeployBusyGate
{
    /// <summary>Marks the app busy until the returned handle is disposed. Dispose is idempotent.</summary>
    IDisposable Hold(DeployBusyWork work, Guid? wallId = null, Guid? userId = null);
}
