// <copyright file="WallRefreshReviewApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST …/refresh/{refreshId}/review/answers</c>.</summary>
/// <param name="Answers">The answers, applied in order: <c>{ key, answer }</c> with the item's <c>key</c> from <c>GET …/review</c>.</param>
public sealed record ReviewAnswersRequest(IReadOnlyList<ReviewAnswerItem>? Answers);

/// <summary>One answer of <see cref="ReviewAnswersRequest"/>.</summary>
/// <param name="Key">The item's key.</param>
/// <param name="Answer">One of the item's <c>answers</c>, or <c>undo</c>.</param>
public sealed record ReviewAnswerItem(
    string? Key,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReviewAnswer>))] ReviewAnswer? Answer);

/// <summary>Body of <c>POST …/refresh/{refreshId}/review/accept</c>: answer a whole group with its suggested answers.</summary>
/// <param name="Group">The group (<c>notFound</c>, <c>leftOut</c>, <c>newHold</c>, …).</param>
/// <param name="PanelId">Only this panel's items, when given.</param>
public sealed record ReviewAcceptRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<ReviewGroup>))] ReviewGroup? Group,
    Guid? PanelId);

/// <summary>What the answer routes answer: how many were written, the counts after, and where it stopped when it did.</summary>
public sealed record ReviewAnswered(int Answered, ReviewCounts? Counts, string? FailedKey = null, string? Error = null);

/// <summary>The review as JSON: groups and answers by name, so a script reads what a person reads.</summary>
public static class ReviewJson
{
    /// <summary>The options the review routes serialise with (enums as camelCase names).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
