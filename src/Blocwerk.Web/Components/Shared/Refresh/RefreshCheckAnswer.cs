// <copyright file="RefreshCheckAnswer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared.Refresh;

/// <summary>An answer picked on a card of the check step.</summary>
/// <param name="CheckId">The card.</param>
/// <param name="Answer">Keep, remove, or take the answer back.</param>
public sealed record RefreshCheckAnswer(Guid CheckId, UpdateExceptionAnswer Answer);
