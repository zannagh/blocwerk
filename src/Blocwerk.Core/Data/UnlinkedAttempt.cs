// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Data;

/// <summary>An attempt no activity entry is linked to yet, as <see cref="AttemptActivityMatch"/> reads it.</summary>
public sealed record UnlinkedAttempt(Guid Id, Guid UserId, Guid BoulderId, AttemptType Type, DateTimeOffset Timestamp);
