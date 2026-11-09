// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Recognition;

/// <summary>Where hold recognition runs (<c>RECOGNITION__MODE</c>: <c>inprocess</c>, <c>queue+fallback</c>, <c>queue</c>).</summary>
public enum RecognitionMode
{
    /// <summary>In the app's process, as before the queue; the runner API is mapped only when runners are on.</summary>
    InProcess,

    /// <summary>Through the queue; an interactive job nobody claims in time runs in the app instead.</summary>
    QueueWithFallback,

    /// <summary>Through the queue only; an interactive job nobody claims in time fails ("recognition unavailable").</summary>
    Queue,
}
