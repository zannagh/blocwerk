// <copyright file="HoldProposalStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Enums;

/// <summary>Review state of a <see cref="Entities.HoldProposal"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HoldProposalStatus>))]
public enum HoldProposalStatus
{
    /// <summary>Waiting for a wall admin; replaced by the next run.</summary>
    Pending = 0,

    /// <summary>Became a hold (<see cref="Entities.HoldProposal.HoldId"/>).</summary>
    Accepted = 1,

    /// <summary>Not a hold; the spot is not proposed again.</summary>
    Rejected = 2,
}
