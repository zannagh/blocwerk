// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Core.Tests.OfflineClientViews;

/// <summary>The error boundary's logger the WebAssembly host provides; the tests read the rendered error instead.</summary>
internal sealed class SilentErrorBoundaryLogger : IErrorBoundaryLogger
{
    public ValueTask LogErrorAsync(Exception exception) => ValueTask.CompletedTask;
}
