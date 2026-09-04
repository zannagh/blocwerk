/*
 * bwPush — the Web Push (RFC 8291 / VAPID) client half.
 *
 * Sibling to bwInstall: same shape, same persistence, same "the Blazor component renders nothing
 * until shouldShow() has answered" contract, so the notification banner can never flash on a device
 * that is already subscribed or ineligible. The install banner and this one deliberately share the
 * install/standalone test — push is only offered on an INSTALLED PWA (Android; iOS >=16.4 added to
 * the home screen), because that is the only context where a browser will deliver a push at all.
 *
 * This file does no network I/O of its own: subscribe()/getExisting()/unsubscribe() return plain
 * objects and the Blazor component persists them server-side through IPushNotificationService, which
 * keeps the whole flow on the circuit and off any CSRF-exposed HTTP endpoint. Persistence here is
 * localStorage only, for the per-device "not now" / "never" choice — the same convention (and the
 * same 14-day snooze) as bwInstall; the server never reads it.
 *
 * Loaded with the deferred end-of-body bundle: unlike bwInstall there is no parse-time event to
 * catch, so it does not need to be in <head>.
 */
window.bwPush = (function () {
    'use strict';

    const NEVER_KEY = 'blocwerk-notify-never';
    const SNOOZE_KEY = 'blocwerk-notify-snoozed-until';

    // Mirrors bwInstall: "Not now" is a not-right-now, so it expires after two weeks.
    const SNOOZE_DAYS = 14;

    function readItem(key) {
        try {
            return localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function writeItem(key, value) {
        try {
            localStorage.setItem(key, value);
        } catch (e) {
            /* storage blocked (private mode) — the banner still hides for this view. */
        }
    }

    function isSupported() {
        return 'serviceWorker' in navigator
            && 'PushManager' in window
            && 'Notification' in window;
    }

    // Same test bwInstall uses: standalone display-mode, or iOS Safari's navigator.standalone. A
    // browser only delivers push to an installed PWA, so this is a hard gate, not a nicety.
    function isInstalled() {
        try {
            if (window.matchMedia && window.matchMedia('(display-mode: standalone)').matches) {
                return true;
            }
        } catch (e) {
            /* matchMedia unavailable — fall through to the iOS check. */
        }

        return window.navigator.standalone === true;
    }

    function isSuppressed() {
        if (readItem(NEVER_KEY) === '1') {
            return true;
        }

        const until = parseInt(readItem(SNOOZE_KEY), 10);
        return !isNaN(until) && Date.now() < until;
    }

    // Standard VAPID applicationServerKey conversion: the public key is a base64url string, and
    // pushManager.subscribe wants the raw bytes as a Uint8Array.
    function urlBase64ToUint8Array(base64String) {
        const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
        const base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
        const raw = window.atob(base64);
        const output = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) {
            output[i] = raw.charCodeAt(i);
        }
        return output;
    }

    // The keys off a PushSubscription are ArrayBuffers; the server stores/uses them as base64url.
    function bufferToBase64Url(buffer) {
        if (!buffer) {
            return null;
        }
        const bytes = new Uint8Array(buffer);
        let binary = '';
        for (let i = 0; i < bytes.length; i++) {
            binary += String.fromCharCode(bytes[i]);
        }
        return window.btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    // Flattens a PushSubscription into the plain { endpoint, p256dh, auth } the component saves. A
    // subscription missing either key is unusable, so it is treated as no subscription at all.
    function toPlain(sub) {
        if (!sub) {
            return null;
        }
        const p256dh = bufferToBase64Url(sub.getKey('p256dh'));
        const auth = bufferToBase64Url(sub.getKey('auth'));
        if (!p256dh || !auth) {
            return null;
        }
        return { endpoint: sub.endpoint, p256dh: p256dh, auth: auth };
    }

    return {
        isSupported: isSupported,

        isInstalled: isInstalled,

        permission: function () {
            try {
                return Notification.permission;
            } catch (e) {
                return 'denied';
            }
        },

        // The component renders nothing until this resolves. Only offer the banner where a
        // subscription can actually be created and the user has not already answered the prompt.
        shouldShow: function () {
            try {
                return isSupported()
                    && isInstalled()
                    && Notification.permission === 'default'
                    && !isSuppressed();
            } catch (e) {
                return false;
            }
        },

        // Asks for permission (a user gesture must be in flight) and creates the push subscription.
        // Returns { ok:false, permission } when permission is not granted, or { ok:true, endpoint,
        // p256dh, auth } on success. Never throws into the caller.
        subscribe: async function (vapidPublicKey) {
            try {
                if (!isSupported() || !vapidPublicKey) {
                    return { ok: false, permission: this.permission() };
                }

                const permission = await Notification.requestPermission();
                if (permission !== 'granted') {
                    return { ok: false, permission: permission };
                }

                const reg = await navigator.serviceWorker.ready;
                let sub = await reg.pushManager.getSubscription();
                if (!sub) {
                    sub = await reg.pushManager.subscribe({
                        userVisibleOnly: true,
                        applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
                    });
                }

                const plain = toPlain(sub);
                if (!plain) {
                    return { ok: false, permission: permission };
                }
                return { ok: true, endpoint: plain.endpoint, p256dh: plain.p256dh, auth: plain.auth };
            } catch (e) {
                return { ok: false, permission: this.permission() };
            }
        },

        // iOS (16.4+, installed PWA) rejects Notification.requestPermission() unless it runs inside a
        // live user gesture. A Blazor @onclick round-trips to the server first, so the activation is
        // already gone by the time the circuit calls back — hence this binds the permission+subscribe
        // straight onto the button's native click, with NO await that crosses the circuit before
        // requestPermission is reached. On success it hands the plain subscription back to .NET via
        // OnDeviceSubscribed; on any failure it reports the permission (or a reason) via OnEnableFailed.
        // Guards against double-binding so re-renders can call it idempotently.
        bindEnable: function (buttonElement, dotNetRef, vapidPublicKey) {
            if (!buttonElement || buttonElement.dataset.bwPushBound === '1') {
                return;
            }
            buttonElement.dataset.bwPushBound = '1';

            buttonElement.addEventListener('click', async function () {
                // Everything up to requestPermission is synchronous — no await crosses the circuit,
                // so the user-activation is still live when the browser sees the request.
                if (!isSupported() || !vapidPublicKey) {
                    dotNetRef.invokeMethodAsync('OnEnableFailed', 'unsupported');
                    return;
                }

                let permission;
                try {
                    permission = await Notification.requestPermission();
                } catch (e) {
                    dotNetRef.invokeMethodAsync('OnEnableFailed', 'error');
                    return;
                }

                if (permission !== 'granted') {
                    dotNetRef.invokeMethodAsync('OnEnableFailed', permission);
                    return;
                }

                try {
                    const reg = await navigator.serviceWorker.ready;
                    let sub = await reg.pushManager.getSubscription();
                    if (!sub) {
                        sub = await reg.pushManager.subscribe({
                            userVisibleOnly: true,
                            applicationServerKey: urlBase64ToUint8Array(vapidPublicKey)
                        });
                    }

                    const plain = toPlain(sub);
                    if (!plain) {
                        dotNetRef.invokeMethodAsync('OnEnableFailed', 'error');
                        return;
                    }

                    dotNetRef.invokeMethodAsync(
                        'OnDeviceSubscribed', plain.endpoint, plain.p256dh, plain.auth, navigator.userAgent || '');
                } catch (e) {
                    dotNetRef.invokeMethodAsync('OnEnableFailed', 'error');
                }
            });
        },

        // The already-subscribed shape, so the component can silently refresh the server row on load
        // (endpoints rotate; a saved subscription can go stale). Null when permission is not granted
        // or nothing is subscribed on this device. Never prompts.
        getExisting: async function () {
            try {
                if (!isSupported() || Notification.permission !== 'granted') {
                    return null;
                }
                const reg = await navigator.serviceWorker.ready;
                const sub = await reg.pushManager.getSubscription();
                return toPlain(sub);
            } catch (e) {
                return null;
            }
        },

        // Unsubscribes this device's PushManager subscription and returns the endpoint that was
        // removed so the caller can delete the matching server row; null when there was nothing.
        unsubscribe: async function () {
            try {
                if (!isSupported()) {
                    return null;
                }
                const reg = await navigator.serviceWorker.ready;
                const sub = await reg.pushManager.getSubscription();
                if (!sub) {
                    return null;
                }
                const endpoint = sub.endpoint;
                await sub.unsubscribe();
                return endpoint;
            } catch (e) {
                return null;
            }
        },

        // navigator.userAgent, so the component can stamp the subscription without its own JS eval.
        userAgent: function () {
            try {
                return navigator.userAgent || '';
            } catch (e) {
                return '';
            }
        },

        snooze: function () {
            writeItem(SNOOZE_KEY, String(Date.now() + SNOOZE_DAYS * 24 * 60 * 60 * 1000));
        },

        never: function () {
            writeItem(NEVER_KEY, '1');
        }
    };
})();
