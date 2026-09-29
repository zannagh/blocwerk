// <copyright file="NewHoldTriageModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>One staged panel's triage: its default discards, its photo size and the unpaired detections it kept (RAW pixels).</summary>
internal sealed record PanelTriage(
    Dictionary<Guid, NewHoldDiscardReason> Discards,
    (int Width, int Height) Size,
    IReadOnlyList<(double X, double Y)> KeptNew);

/// <summary>The triaged staged centre, as a neighbour's triage needs it: its holds by id, photo size and kept new holds.</summary>
internal sealed record TriagedCentre(
    IReadOnlyDictionary<Guid, Hold> Holds,
    (int Width, int Height) Size,
    IReadOnlyList<(double X, double Y)> KeptNew);
