// <copyright file="HangingJsRuntime.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.JSInterop;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The browser of a retained circuit whose phone is locked: every interop call is accepted and never answered, and the
/// cancellation token is ignored (the worst case, so the caller's own bound is what is tested).
/// </summary>
internal sealed class HangingJsRuntime : IJSRuntime
{
    public int Calls { get; private set; }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
    {
        Calls++;
        return new ValueTask<TValue>(new TaskCompletionSource<TValue>().Task);
    }

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
    {
        return InvokeAsync<TValue>(identifier, args);
    }
}
