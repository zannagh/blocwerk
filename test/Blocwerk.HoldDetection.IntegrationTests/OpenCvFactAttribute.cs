// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.HoldDetection.Tests;

/// <summary>
/// A <c>[Fact]</c> that needs OpenCV's native library, which only ships for Linux (the OpenCvSharp runtimes of
/// Blocwerk.HoldDetection), so it is skipped on macOS. CI runs it on Linux.
/// </summary>
public sealed class OpenCvFactAttribute : FactAttribute
{
    /// <summary>Why the test is skipped on macOS.</summary>
    public const string MacSkipReason =
        "OpenCV's native library ships for Linux only. On a Mac, run recognition through the worker container (README).";

    public OpenCvFactAttribute()
    {
        if (OperatingSystem.IsMacOS())
        {
            Skip = MacSkipReason;
        }
    }
}
