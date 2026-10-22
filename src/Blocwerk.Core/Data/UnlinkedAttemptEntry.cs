// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Data;

/// <summary>An <c>AttemptLogged</c> entry not linked to its attempt yet, as <see cref="AttemptActivityMatch"/> reads it.</summary>
public sealed record UnlinkedAttemptEntry(Guid Id, Guid UserId, Guid? BoulderId, string? Details, DateTimeOffset Timestamp);
