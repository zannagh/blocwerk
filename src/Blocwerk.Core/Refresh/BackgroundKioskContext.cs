// <copyright file="BackgroundKioskContext.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Refresh;

/// <summary>The kiosk context of a background step: never a kiosk (the run was started from an admin's own device).</summary>
internal sealed class BackgroundKioskContext : IKioskContext
{
    public bool IsKiosk => false;

    public Guid? KioskWallId => null;

    public Guid? KioskApiKeyId => null;

    public Task InitializeAsync() => Task.CompletedTask;
}
