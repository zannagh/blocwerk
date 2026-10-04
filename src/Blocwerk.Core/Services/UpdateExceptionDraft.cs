// <copyright file="UpdateExceptionDraft.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>A confirm-screen card before it is stored (<see cref="Entities.WallUpdateException"/>; same fields).</summary>
public sealed record UpdateExceptionDraft(
    UpdateExceptionKind Kind,
    Guid? OldHoldId,
    Guid? StagedHoldId,
    Guid? PanelId = null,
    double? X = null,
    double? Y = null,
    Guid? ModelId = null,
    string? FacetId = null,
    double? A = null,
    double? B = null,
    double? PhotoScore = null,
    double? TextureScore = null,
    double? Confidence = null);

/// <summary>A stored confirm-screen card, as the page and the summary read it.</summary>
/// <param name="Id">The card.</param>
/// <param name="Kind">Why it needs a look.</param>
/// <param name="OldHoldId">The old hold, when the card is about one.</param>
/// <param name="StagedHoldId">The staged hold, when the card is about one.</param>
/// <param name="Status">Whether, and how, it was answered.</param>
/// <param name="PhotoScore">Old photo against the new photo.</param>
/// <param name="TextureScore">Old photo against the 3D texture.</param>
/// <param name="Confidence">The matcher's confidence.</param>
/// <param name="HasTexture">A 3D texture spot is known for it.</param>
public sealed record UpdateExceptionInfo(
    Guid Id,
    UpdateExceptionKind Kind,
    Guid? OldHoldId,
    Guid? StagedHoldId,
    UpdateExceptionStatus Status,
    double? PhotoScore = null,
    double? TextureScore = null,
    double? Confidence = null,
    bool HasTexture = false);
