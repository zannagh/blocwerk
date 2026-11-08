// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.HoldDetection.Tests;

/// <summary>A <c>[Theory]</c> that needs OpenCV's native library and is skipped on macOS, like <see cref="OpenCvFactAttribute"/>.</summary>
public sealed class OpenCvTheoryAttribute : TheoryAttribute
{
    public OpenCvTheoryAttribute()
    {
        if (OperatingSystem.IsMacOS())
        {
            Skip = OpenCvFactAttribute.MacSkipReason;
        }
    }
}
