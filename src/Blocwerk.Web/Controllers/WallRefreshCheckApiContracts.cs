// <copyright file="WallRefreshCheckApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST …/refresh/{refreshId}/checks/{checkId}/answer</c>.</summary>
/// <param name="Answer">
/// <c>keep</c> (keep the old hold, confirm the match, or keep the detection), <c>remove</c> (remove the old hold, or leave
/// the detection out; not taken for every kind), or <c>undo</c> (the update's default back). Names or 0/1/2.
/// </param>
public sealed record RefreshCheckAnswerRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<UpdateExceptionAnswer>))] UpdateExceptionAnswer? Answer);

/// <summary>Body of <c>POST …/refresh/{refreshId}/checks/answers</c>.</summary>
/// <param name="Answers">The answers, applied in order.</param>
public sealed record RefreshCheckAnswersRequest(IReadOnlyList<RefreshCheckAnswerItem>? Answers);

/// <summary>One answer of <see cref="RefreshCheckAnswersRequest"/>.</summary>
/// <param name="CheckId">The card (<c>id</c> from <c>GET …/checks</c>).</param>
/// <param name="Answer">As in <see cref="RefreshCheckAnswerRequest"/>.</param>
public sealed record RefreshCheckAnswerItem(
    Guid CheckId,
    [property: JsonConverter(typeof(JsonStringEnumConverter<UpdateExceptionAnswer>))] UpdateExceptionAnswer? Answer);

/// <summary>
/// What the answer routes answer: how many cards were answered (the summary is being made again). When an answer was
/// refused, also which card and why; the answers before it are saved, it and those after it are not (202 when some were
/// saved, 409 when none were).
/// </summary>
/// <param name="Answered">The number of answers written, in order.</param>
/// <param name="FailedCheckId">The card whose answer was refused; null when all were written.</param>
/// <param name="Error">Why it was refused; null when all were written.</param>
public sealed record RefreshCheckAnswered(int Answered, Guid? FailedCheckId = null, string? Error = null);
