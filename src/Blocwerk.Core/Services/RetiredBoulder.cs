// <copyright file="RetiredBoulder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A boulder a hold delete made historic (see <see cref="IWallService.DeleteHoldRetiringAsync"/>).</summary>
public sealed record RetiredBoulder(Guid Id, string Name);
