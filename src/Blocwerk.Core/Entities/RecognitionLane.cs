// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Entities;

/// <summary>The priority lane of a <see cref="RecognitionJob"/>: a claim takes interactive jobs before background ones.</summary>
public enum RecognitionLane
{
    /// <summary>A person waits for it: short lease, claim deadline, two attempts.</summary>
    Interactive = 0,

    /// <summary>Follow-up work nobody waits for: longer lease, stays queued, three attempts.</summary>
    Background = 1,
}
