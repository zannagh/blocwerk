// <copyright file="CallbackDisposable.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>Runs a callback once, on the first <see cref="Dispose"/>.</summary>
internal sealed class CallbackDisposable(Action callback) : IDisposable
{
    private Action? callback = callback;

    public void Dispose()
    {
        Interlocked.Exchange(ref callback, null)?.Invoke();
    }
}
