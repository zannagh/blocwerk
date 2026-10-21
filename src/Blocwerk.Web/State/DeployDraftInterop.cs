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
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Keeps <paramref name="payloadJson"/> under <paramref name="key"/> and queues <paramref name="notice"/>.</summary>
    public static async Task<bool> KeepAsync(IJSRuntime js, string key, string payloadJson, string notice, string? href = null)
    {
        return await TryAsync(js, "bwDeployDrafts.keep", key, payloadJson, notice, href);
    }

    /// <summary>Queues a notice shown on the next page load (the reload after the deploy).</summary>
    public static async Task<bool> NoticeAsync(IJSRuntime js, string notice, string? href = null)
    {
        return await TryAsync(js, "bwDeployDrafts.notice", notice, href);
    }

    /// <summary>Shows a notice right now (for state the server already saved while the browser was away).</summary>
    public static async Task<bool> ShowAsync(IJSRuntime js, string notice, string? href = null)
    {
        return await TryAsync(js, "bwDeployDrafts.show", notice, href);
    }

    /// <summary>Forgets the kept state under <paramref name="key"/> (saved, or deliberately left).</summary>
    public static async Task DropAsync(IJSRuntime js, string key)
    {
        await TryAsync(js, "bwDeployDrafts.drop", key);
    }

    /// <summary>Takes (and forgets) the state kept under <paramref name="key"/>, or null.</summary>
    public static async Task<string?> TakeAsync(IJSRuntime js, string key)
    {
        try
        {
            using var cts = new CancellationTokenSource(CallTimeout);
            return await js.InvokeAsync<string?>("bwDeployDrafts.take", cts.Token, key);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> TryAsync(IJSRuntime js, string identifier, params object?[] args)
    {
        try
        {
            using var cts = new CancellationTokenSource(CallTimeout);
            await js.InvokeVoidAsync(identifier, cts.Token, args);
            return true;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException)
        {
            return false;
        }
    }
}
