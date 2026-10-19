// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>One journal batch as the admin listing shows it.</summary>
public sealed record ChangeJournalBatchSummary(
    Guid Id,
    string Label,
    ChangeJournalScopeKind ScopeKind,
    Guid? ScopeId,
    ChangeJournalStatus Status,
    string? Actor,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SealedAt,
    int EntryCount);
