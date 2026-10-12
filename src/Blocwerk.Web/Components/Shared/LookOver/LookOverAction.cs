// <copyright file="LookOverAction.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>
/// Something a part of the look-over page wants done: the page runs it like every answer (one at a time, then the
/// list as it is now, a refusal said in the page's one place for it).
/// </summary>
/// <param name="FindingId">The finding it is about, or null for a step about many.</param>
/// <param name="Run">What to do.</param>
public sealed record LookOverAction(Guid? FindingId, Func<Task> Run);
