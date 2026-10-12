// <copyright file="LookOverBoulder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>A boulder that uses a finding's hold.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="NeedsReview">It is marked for review.</param>
public sealed record LookOverBoulder(Guid Id, string Name, bool NeedsReview);
