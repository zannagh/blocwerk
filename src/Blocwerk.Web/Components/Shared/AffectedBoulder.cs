// <copyright file="AffectedBoulder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>A boulder a flushed wall batch retired or flagged, for the notice's link list.</summary>
public sealed record AffectedBoulder(Guid Id, string Name);
