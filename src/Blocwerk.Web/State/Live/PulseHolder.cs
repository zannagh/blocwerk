// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Live;

/// <summary>
/// A component's one pulse subscription (a <see cref="PulseWake"/> or a <see cref="PulseLoop"/>) for the wall it shows.
/// Opening replaces and disposes the previous one. Once the component is disposed nothing new is opened, and a
/// subscription that was still being opened when the component went, or when the wall changed meanwhile, is disposed the
/// moment it lands: a page never leaks a subscription opened after one of its awaits.
/// </summary>
/// <typeparam name="T">The subscription.</typeparam>
public sealed class PulseHolder<T> : IDisposable
    where T : class, IDisposable
{
    private readonly Lock gate = new();
    private T? current;
    private Guid? wallId;
    private int version;
    private bool disposed;

    /// <summary>The open subscription, if any.</summary>
    public T? Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>Whether the open subscription is for <paramref name="wall"/>.</summary>
    public bool IsFor(Guid wall)
    {
        lock (gate)
        {
            return current is not null && wallId == wall;
        }
    }

    /// <summary>Opens <paramref name="open"/> for <paramref name="wall"/> unless the component went; false when it did.</summary>
    public bool Open(Guid wall, Func<T> open)
    {
        T? previous;
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            previous = current;
            current = open();
            wallId = wall;
            version++;
        }

        previous?.Dispose();
        return true;
    }

    /// <summary>
    /// Opens asynchronously. The previous subscription goes at once; the new one is kept only if, when it lands, the
    /// component is still there and no later open started. Otherwise it is disposed and the result is null.
    /// </summary>
    public async Task<T?> OpenAsync(Guid wall, Func<Task<T>> open)
    {
        T? previous;
        int mine;
        lock (gate)
        {
            if (disposed)
            {
                return null;
            }

            previous = current;
            current = null;
            wallId = wall;
            mine = ++version;
        }

        previous?.Dispose();
        var opened = await open();
        lock (gate)
        {
            if (!disposed && version == mine)
            {
                current = opened;
                return opened;
            }
        }

        opened.Dispose();
        return null;
    }

    /// <summary>Disposes the open subscription (or the one being opened, when it lands); later opens still work.</summary>
    public void Close()
    {
        T? previous;
        lock (gate)
        {
            previous = current;
            current = null;
            wallId = null;
            version++;
        }

        previous?.Dispose();
    }

    public void Dispose()
    {
        T? previous;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            previous = current;
            current = null;
        }

        previous?.Dispose();
    }
}
