// <copyright file="LookOverAnswerApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST …/look-over/findings/{findingId}/answer</c>.</summary>
/// <param name="Answer">
/// One of the finding's <c>answers</c>: <c>Settle</c>, <c>DifferentHold</c>, <c>RemoveHold</c>, <c>UseNewHold</c>,
/// <c>AddHold</c>, <c>Unlink</c> (names or numbers).
/// </param>
/// <param name="ExpectedBoulderIds">
/// The ids of the finding's <c>boulders</c> as read. Required for every answer but <c>Settle</c>: when other boulders
/// use the hold by now, the answer is refused (409) and the caller reads the findings again.
/// </param>
/// <param name="NewHoldBoulderIds">
/// With <c>UseNewHold</c> only, asked per boulder ("Should this boulder use the new hold in that place?"): the boulders
/// that should use the new hold, a subset of <c>expectedBoulderIds</c> (400 otherwise). The others become historic with
/// the old hold, which is removed; an empty list is <c>RemoveHold</c>. Omitted or null moves every boulder (the merge).
/// </param>
public sealed record LookOverAnswerRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<LookOverAnswer>))] LookOverAnswer? Answer,
    IReadOnlyCollection<Guid>? ExpectedBoulderIds = null,
    IReadOnlyCollection<Guid>? NewHoldBoulderIds = null);

/// <summary>Body of <c>POST …/look-over/finish</c>.</summary>
/// <param name="LeaveOpen">Finish although findings are open (they stay open and listed). Defaults to false.</param>
public sealed record LookOverFinishRequest(bool LeaveOpen = false);
