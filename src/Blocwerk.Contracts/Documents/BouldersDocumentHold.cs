// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Documents;

/// <summary>One hold of a boulder: a reference into the <see cref="HoldsDocument"/> and its role.</summary>
/// <param name="HoldId">The hold.</param>
/// <param name="Type">Its role, by name (<c>Start</c>, <c>Top</c>, ...).</param>
/// <param name="Usage">Its usage, by name (<c>HandAndFoot</c>, ...).</param>
public sealed record BouldersDocumentHold(Guid HoldId, string Type, string Usage);
