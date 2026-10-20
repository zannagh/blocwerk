// <copyright file="HoldSplitBoulder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A boulder on a hold being split.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="IsHistoric">True for an archived or historic boulder (a frozen record, moved like the others).</param>
public sealed record HoldSplitBoulder(Guid Id, string Name, bool IsHistoric);
