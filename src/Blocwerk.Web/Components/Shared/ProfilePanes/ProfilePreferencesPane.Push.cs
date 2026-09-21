// <copyright file="ProfilePreferencesPane.Push.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Shared.ProfilePanes;

/// <summary>
/// Device-level push-notification enablement for the profile preferences pane: detecting whether this
/// device still needs to be subscribed, and the gesture-bound enable path shared with NotificationPrompt.
/// </summary>
public partial class ProfilePreferencesPane
{
    /// <inheritdoc />
    public void Dispose()
    {
        pushDotNetRef?.Dispose();
    }

    /// <summary>
    /// Called from bwPush.bindEnable's gesture handler on a successful subscribe. Persists the
    /// device's subscription and hides the control.
    /// </summary>
    [JSInvokable]
    public async Task OnDeviceSubscribed(string endpoint, string p256dh, string auth, string userAgent)
    {
        pushEnableError = null;
        try
        {
            await Push.SaveSubscriptionAsync(Viewer.Id, endpoint, p256dh, auth, userAgent);
            showEnablePush = false;
            pushEnableInfo = "Notifications are on for this device.";
        }
        catch (Exception)
        {
            pushEnableError = "Couldn't enable notifications on this device. Please try again.";
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Called from the gesture handler when permission wasn't granted or the subscribe failed.
    /// </summary>
    [JSInvokable]
    public async Task OnEnableFailed(string permission)
    {
        if (permission == "denied")
        {
            pushEnableError = "This device has blocked notifications. Turn them on in your browser settings.";
            showEnablePush = false;
        }
        else
        {
            pushEnableError = "Couldn't enable notifications on this device. Please try again.";
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Read the client-side zoom-lens pref once the circuit can call into JS. Defensive: any
        // interop failure just leaves the default in place.
        if (firstRender)
        {
            try
            {
                zoomLensMag = await JS.InvokeAsync<int>("bwPrefs.getZoomLensMag");
                toolbarPlacement = await JS.InvokeAsync<string>("bwPrefs.getToolbarPlacement");
                StateHasChanged();
            }
            catch (Exception)
            {
                /* JS unavailable (prerender/circuit gone) — keep the default. */
            }

            await DetectPushEnableAsync();
        }

        // Bind the native click on the "enable on this device" button the render after it appears.
        if (showEnablePush && !pushEnableBound)
        {
            pushEnableBound = true;
            try
            {
                pushDotNetRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("bwPush.bindEnable", pushEnableButton, pushDotNetRef, Push.PublicKey);
            }
            catch (Exception)
            {
                /* interop gone — leave the button inert. */
            }
        }
    }

    // Decides whether to offer the device-level "enable notifications" control: installed + supported,
    // VAPID configured, and this device not already subscribed (permission not granted, or the
    // PushManager subscription is gone — a state the transient banner's default-permission gate never
    // re-offers). Silent on any interop failure.
    private async Task DetectPushEnableAsync()
    {
        if (Push.PublicKey is null)
        {
            return;
        }

        try
        {
            if (!await JS.InvokeAsync<bool>("bwPush.isSupported")
                || !await JS.InvokeAsync<bool>("bwPush.isInstalled"))
            {
                return;
            }

            bool needsEnable;
            var permission = await JS.InvokeAsync<string>("bwPush.permission");
            if (permission != "granted")
            {
                needsEnable = true;
            }
            else
            {
                var existing = await JS.InvokeAsync<PushSub?>("bwPush.getExisting");
                needsEnable = existing is null || !existing.IsComplete;
            }

            if (needsEnable)
            {
                showEnablePush = true;
                StateHasChanged();
            }
        }
        catch (Exception)
        {
            /* interop gone — leave the control hidden. */
        }
    }
}
