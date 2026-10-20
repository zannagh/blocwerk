// <copyright file="HoldProposalStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace Blocwerk.Core.Enums;

/// <summary>Review state of a <see cref="Entities.HoldProposal"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HoldProposalStatus>))]
public enum HoldProposalStatus
{
    /// <summary>Waiting for a wall admin; a later search updates it in place when it finds the same spot.</summary>
    Pending = 0,

    /// <summary>Became a hold (<see cref="Entities.HoldProposal.HoldId"/>).</summary>
    Accepted = 1,

    /// <summary>Not a hold; the spot is not proposed again.</summary>
    Rejected = 2,

    /// <summary>
    /// Taken out of the review by a search, not by a person: it belonged to a superseded 3D model and the new model's pass
    /// does not see it, or another pending proposal is on the same spot. Unlike a rejection it does not keep the spot from
    /// being proposed again.
    /// </summary>
    Retired = 3,
}
