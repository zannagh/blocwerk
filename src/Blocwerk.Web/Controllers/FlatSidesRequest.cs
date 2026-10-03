// <copyright file="FlatSidesRequest.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Controllers;

/// <summary>Body of the flat-sides endpoints (<see cref="WallVolumesController"/>).</summary>
/// <param name="Value">Flat sides on or off.</param>
/// <param name="ApplyToAll">For the wall setting only: also switch the existing volumes.</param>
public sealed record FlatSidesRequest(bool Value, bool ApplyToAll = false);
