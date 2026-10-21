// <copyright file="DeployDraftInterop.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.JSInterop;

namespace Blocwerk.Web.State;

/// <summary>
/// Exception-swallowing bridge to <c>window.bwDeployDrafts</c> (deploy-drafts.js): editor state kept in the browser
/// across a deploy's reload, and the notice shown after it. Every call has its own short timeout, because a pre-deploy
/// flush may reach a circuit whose browser is asleep, and interop with it would otherwise wait for a reconnect.
/// </summary>
public static class DeployDraftInterop
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Keeps <paramref name="payloadJson"/> under <paramref name="key"/> and queues <paramref name="notice"/>.</summary>
    public static async Task<bool> KeepAsync(IJSRuntime js, string key, string payloadJson, string notice, string? href = null)
    {
        return await TryAsync(js, DefaultTimeout, "bwDeployDrafts.keep", key, payloadJson, notice, href);
    }

    /// <summary>Queues a notice shown on the next page load (the reload after the deploy).</summary>
    public static async Task<bool> NoticeAsync(IJSRuntime js, string notice, string? href = null)
    {
        return await TryAsync(js, DefaultTimeout, "bwDeployDrafts.notice", notice, href);
    }

    /// <summary>Shows a notice right now (for state the server already saved while the browser was away).</summary>
    public static async Task<bool> ShowAsync(IJSRuntime js, string notice, string? href = null)
    {
        return await TryAsync(js, DefaultTimeout, "bwDeployDrafts.show", notice, href);
    }

    /// <summary>
    /// After a server-side save: forget any copy kept on the device and queue the notice, both at once and bounded,
    /// so a browser that is away (a locked phone) costs at most one timeout and never undoes the save.
    /// </summary>
    public static async Task TellSavedAsync(IJSRuntime js, string key, string notice, string? href, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        await Task.WhenAll(
            TryAsync(js, limit, "bwDeployDrafts.drop", key),
            TryAsync(js, limit, "bwDeployDrafts.notice", notice, href));
    }

    /// <summary>Forgets the kept state under <paramref name="key"/> (saved, or deliberately left).</summary>
    public static async Task DropAsync(IJSRuntime js, string key)
    {
        await TryAsync(js, DefaultTimeout, "bwDeployDrafts.drop", key);
    }

    /// <summary>Takes (and forgets) the state kept under <paramref name="key"/>, or null.</summary>
    public static async Task<string?> TakeAsync(IJSRuntime js, string key)
    {
        try
        {
            using var cts = new CancellationTokenSource(DefaultTimeout);
            return await js.InvokeAsync<string?>("bwDeployDrafts.take", cts.Token, key).AsTask().WaitAsync(DefaultTimeout);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return null;
        }
    }

    // Bounded twice over: the token for a runtime that honours it, WaitAsync for one that does not.
    private static async Task<bool> TryAsync(IJSRuntime js, TimeSpan timeout, string identifier, params object?[] args)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await js.InvokeVoidAsync(identifier, cts.Token, args).AsTask().WaitAsync(timeout);
            return true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }
}
